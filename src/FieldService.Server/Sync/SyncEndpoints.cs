using System.Text.Json;
using FieldService.Server.Data;
using FieldService.Shared.Domain;
using FieldService.Shared.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Server.Sync;

public static class SyncEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var sync = app.MapGroup("/sync").AddEndpointFilter(ProtocolCheck);

        sync.MapGet("/capabilities", (AppDbContext db) => Results.Ok(new CapabilitiesResponse(
            MinProtocolVersion: SyncProtocol.MinimumSupported,
            MaxProtocolVersion: SyncProtocol.Current,
            ServerVersionHighWaterMark: db.SyncSequence.AsNoTracking().Single(s => s.Id == 1).NextVersion - 1,
            ServerTimeUtc: DateTime.UtcNow)));

        sync.MapPost("/pull", PullHandler);
        sync.MapPost("/push", PushHandler);

        return app;
    }

    /// <summary>
    /// Rejects requests whose protocol header is out of our support window. This is how you
    /// prevent an old client from corrupting data after a breaking schema change.
    /// </summary>
    private static async ValueTask<object?> ProtocolCheck(EndpointFilterInvocationContext ctx,
                                                           EndpointFilterDelegate next)
    {
        var header = ctx.HttpContext.Request.Headers[SyncProtocol.HeaderName].FirstOrDefault();
        if (!int.TryParse(header, out var v) || v < SyncProtocol.MinimumSupported || v > SyncProtocol.Current)
            return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
        return await next(ctx);
    }

    // ---------------------------------------------------------------------------------
    // PULL: return all rows with Version > SinceVersion, batched.
    // ---------------------------------------------------------------------------------
    private static async Task<IResult> PullHandler(PullRequest req, AppDbContext db, CancellationToken ct)
    {
        var batch = Math.Clamp(req.MaxBatchSize, 50, 1000);
        var changes = new List<EntityChange>(batch);

        // We pull each entity kind independently and then sort-merge by Version. For a real
        // deployment consider a ChangeFeed table that writes on every SaveChanges so the pull
        // is a single indexed query. Here we optimize for legibility.
        await Collect<Customer>(db.Customers, EntityKind.Customer, req.SinceVersion, batch, changes, ct);
        await Collect<Technician>(db.Technicians, EntityKind.Technician, req.SinceVersion, batch, changes, ct);
        await Collect<Part>(db.Parts, EntityKind.Part, req.SinceVersion, batch, changes, ct);
        await Collect<WorkOrder>(db.WorkOrders, EntityKind.WorkOrder, req.SinceVersion, batch, changes, ct);
        await Collect<WorkOrderLineItem>(db.LineItems, EntityKind.WorkOrderLineItem, req.SinceVersion, batch, changes, ct);

        changes.Sort((a, b) => a.Version.CompareTo(b.Version));
        var trimmed = changes.Take(batch).ToList();
        var hasMore = changes.Count > batch;

        var highWater = (await db.SyncSequence.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).NextVersion - 1;
        return Results.Ok(new PullResponse(highWater, trimmed, hasMore));
    }

    private static async Task Collect<T>(IQueryable<T> src, EntityKind kind, long since, int batch,
                                         List<EntityChange> sink, CancellationToken ct)
        where T : class, ISyncableEntity
    {
        var rows = await src.AsNoTracking()
                            .Where(x => x.Version > since)
                            .OrderBy(x => x.Version)
                            .Take(batch)
                            .ToListAsync(ct);

        foreach (var row in rows)
        {
            sink.Add(new EntityChange(
                kind,
                row.Id,
                row.Version,
                row.IsDeleted,
                row.IsDeleted ? "" : JsonSerializer.Serialize((object)row, Json)));
        }
    }

    // ---------------------------------------------------------------------------------
    // PUSH: apply client mutations with per-mutation outcome.
    // ---------------------------------------------------------------------------------
    private static async Task<IResult> PushHandler(PushRequest req, AppDbContext db, CancellationToken ct)
    {
        var results = new List<MutationResult>(req.Mutations.Count);

        foreach (var m in req.Mutations)
        {
            // Idempotency: have we already applied this exact mutation?
            var seen = await db.ProcessedMutations.FindAsync(new object?[] { m.ClientMutationId }, ct);
            if (seen is not null)
            {
                results.Add(new MutationResult(m.ClientMutationId, MutationOutcome.Duplicate,
                                               seen.ResultingVersion, null, null));
                continue;
            }

            try
            {
                var outcome = await ApplyOneAsync(db, m, ct);
                results.Add(outcome);

                if (outcome.Outcome is MutationOutcome.Applied)
                {
                    db.ProcessedMutations.Add(new ProcessedMutation
                    {
                        ClientMutationId = m.ClientMutationId,
                        ResultingVersion = outcome.NewVersion!.Value,
                        ProcessedAtUtc = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                results.Add(new MutationResult(m.ClientMutationId, MutationOutcome.Rejected,
                                               null, null, ex.Message));
            }
        }

        var highWater = (await db.SyncSequence.AsNoTracking().SingleAsync(s => s.Id == 1, ct)).NextVersion - 1;
        return Results.Ok(new PushResponse(results, highWater));
    }

    private static async Task<MutationResult> ApplyOneAsync(AppDbContext db, Mutation m, CancellationToken ct)
    {
        switch (m.Entity)
        {
            case EntityKind.WorkOrder:       return await UpsertAsync<WorkOrder>(db, db.WorkOrders, m, ct);
            case EntityKind.WorkOrderLineItem: return await UpsertLineItemAsync(db, m, ct);
            case EntityKind.Customer:        return await UpsertAsync<Customer>(db, db.Customers, m, ct);
            case EntityKind.Part:            return await UpsertAsync<Part>(db, db.Parts, m, ct);
            case EntityKind.Technician:      return await UpsertAsync<Technician>(db, db.Technicians, m, ct);
            default:
                return new MutationResult(m.ClientMutationId, MutationOutcome.Rejected, null, null,
                                          $"Unknown entity kind {m.Entity}");
        }
    }

    private static async Task<MutationResult> UpsertAsync<T>(AppDbContext db, DbSet<T> set,
                                                             Mutation m, CancellationToken ct)
        where T : class, ISyncableEntity
    {
        var existing = await set.FirstOrDefaultAsync(e => e.Id == m.EntityId, ct);

        if (m.Kind == MutationKind.Delete)
        {
            if (existing is null) return new(m.ClientMutationId, MutationOutcome.Applied, null, null, null);
            if (m.BaseVersion is not null && existing.Version != m.BaseVersion)
                return Conflict(m, existing);
            existing.IsDeleted = true;
            existing.Version = await db.AllocateVersionsAsync(1, ct);
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new(m.ClientMutationId, MutationOutcome.Applied, existing.Version, null, null);
        }

        var incoming = JsonSerializer.Deserialize<T>(m.PayloadJson, Json)
                       ?? throw new InvalidOperationException("Empty payload on upsert.");

        if (existing is null)
        {
            if (m.Kind != MutationKind.Insert)
                return new(m.ClientMutationId, MutationOutcome.Rejected, null, null,
                           "Update against missing row.");
            incoming.Version = await db.AllocateVersionsAsync(1, ct);
            incoming.UpdatedAt = DateTime.UtcNow;
            set.Add(incoming);
            await db.SaveChangesAsync(ct);
            return new(m.ClientMutationId, MutationOutcome.Applied, incoming.Version, null, null);
        }

        if (m.BaseVersion is not null && existing.Version != m.BaseVersion)
            return Conflict(m, existing);

        // Copy non-sync fields. In production you'd code-gen this or use Automapper; here we
        // leverage JSON as a poor-man's deep copy (parse into existing's tracker).
        var json = JsonSerializer.Serialize((object)incoming, Json);
        db.Entry(existing).CurrentValues.SetValues(JsonSerializer.Deserialize<T>(json, Json)!);

        existing.Version = await db.AllocateVersionsAsync(1, ct);
        existing.UpdatedAt = DateTime.UtcNow;
        existing.IsDeleted = false;
        await db.SaveChangesAsync(ct);
        return new(m.ClientMutationId, MutationOutcome.Applied, existing.Version, null, null);
    }

    /// <summary>Line items additionally adjust part stock when accepted.</summary>
    private static async Task<MutationResult> UpsertLineItemAsync(AppDbContext db, Mutation m, CancellationToken ct)
    {
        var baseResult = await UpsertAsync<WorkOrderLineItem>(db, db.LineItems, m, ct);
        if (baseResult.Outcome != MutationOutcome.Applied) return baseResult;

        // If this mutation carried a stock delta, decrement the part stock atomically.
        var line = await db.LineItems.AsNoTracking().SingleAsync(l => l.Id == m.EntityId, ct);
        if (line.Kind == LineItemKind.Part && line.PartId is Guid partId && line.StockDelta != 0)
        {
            var part = await db.Parts.SingleAsync(p => p.Id == partId, ct);
            part.StockOnHand += line.StockDelta;
            part.Version = await db.AllocateVersionsAsync(1, ct);
            part.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return baseResult;
    }

    private static MutationResult Conflict<T>(Mutation m, T current) where T : class, ISyncableEntity =>
        new(m.ClientMutationId,
            MutationOutcome.Conflict,
            current.Version,
            JsonSerializer.Serialize((object)current, Json),
            $"Base version {m.BaseVersion} is stale; current is {current.Version}.");
}
