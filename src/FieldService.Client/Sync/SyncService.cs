using System.Net.Http.Json;
using System.Text.Json;
using FieldService.Client.Data;
using FieldService.Shared.Domain;
using FieldService.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Sync;

/// <summary>
/// The whole offline/online brain lives here.
///
/// Flow:
///   1. On Start(): subscribe to the browser online/offline events via JS interop (elided),
///      kick a first sync, then schedule periodic attempts (e.g. every 60s) while online.
///   2. On each SyncOnceAsync():
///        a. Hit /sync/capabilities. If the server rejects our protocol version, set
///           SyncState.ProtocolMismatch and stop — the UI should prompt the user to reload
///           so the service worker pulls down the new app shell.
///        b. PUSH: batch Pending outbox entries (say 50 at a time) to /sync/push, apply
///           outcomes: Applied -> Confirmed + row's Version updated; Duplicate -> Confirmed;
///           Conflict -> keep row but mark Status=Conflicted and store server payload;
///           Rejected -> Status=Failed, exponential backoff on next attempt.
///        c. PULL: loop /sync/pull from cursor until HasMore == false, writing rows into
///           local SQLite. Each batch advances the cursor inside one transaction.
///   3. When Pulling finishes, raise an event so open Razor pages can re-query.
/// </summary>
public sealed class SyncService : ISyncService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IServiceProvider _sp;           // used to open a fresh DbContext per cycle
    private readonly ILogger<SyncService> _log;
    private CancellationTokenSource? _loop;

    public event Action<SyncState>? StateChanged;

    public SyncService(HttpClient http, IServiceProvider sp, ILogger<SyncService> log)
    {
        _http = http;
        _sp = sp;
        _log = log;
        _http.DefaultRequestHeaders.Remove(SyncProtocol.HeaderName);
        _http.DefaultRequestHeaders.Add(SyncProtocol.HeaderName, SyncProtocol.Current.ToString());
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_loop.Token));
    }

    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncOnceAsync(ct); }
            catch (HttpRequestException) { Raise(SyncState.Offline); }
            catch (Exception ex) { _log.LogError(ex, "Sync cycle failed"); Raise(SyncState.Error); }
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        }
    }

    public async Task<SyncRunResult> SyncOnceAsync(CancellationToken ct = default)
    {
        // Capabilities handshake
        try
        {
            var caps = await _http.GetFromJsonAsync<CapabilitiesResponse>("/sync/capabilities",
                                                                          Json, ct);
            if (caps is null || caps.MaxProtocolVersion < SyncProtocol.Current
                             || caps.MinProtocolVersion > SyncProtocol.Current)
            {
                Raise(SyncState.ProtocolMismatch);
                return new(0, 0, 0);
            }
        }
        catch (HttpRequestException) { Raise(SyncState.Offline); throw; }

        var pushed = await PushAsync(ct);
        var pulled = await PullAsync(ct);
        Raise(SyncState.Idle);
        return new(pushed.PushedCount, pulled, pushed.ConflictCount);
    }

    // ---------------------------------------------------------------------------------
    // PUSH
    // ---------------------------------------------------------------------------------
    private async Task<(int PushedCount, int ConflictCount)> PushAsync(CancellationToken ct)
    {
        Raise(SyncState.Pushing);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var batch = await db.Outbox
            .Where(e => e.Status == OutboxStatus.Pending || e.Status == OutboxStatus.Sending)
            .OrderBy(e => e.Id)
            .Take(50)
            .ToListAsync(ct);
        if (batch.Count == 0) return (0, 0);

        foreach (var e in batch) { e.Status = OutboxStatus.Sending; e.AttemptCount++; e.LastAttemptAtUtc = DateTime.UtcNow; }
        await db.SaveChangesAsync(ct);

        var req = new PushRequest(batch.Select(e => new Mutation(
            e.ClientMutationId, e.Entity, e.EntityId, e.Kind, e.BaseVersion, e.PayloadJson, e.CreatedAtUtc
        )).ToList());

        var resp = await _http.PostAsJsonAsync("/sync/push", req, Json, ct);
        resp.EnsureSuccessStatusCode();
        var body = (await resp.Content.ReadFromJsonAsync<PushResponse>(Json, ct))!;

        var pushed = 0;
        var conflicts = 0;
        foreach (var result in body.Results)
        {
            var entry = batch.Single(e => e.ClientMutationId == result.ClientMutationId);
            switch (result.Outcome)
            {
                case MutationOutcome.Applied:
                case MutationOutcome.Duplicate:
                    entry.Status = OutboxStatus.Confirmed;
                    entry.LastError = null;
                    // Stamp our local row's Version with the server's authoritative number so
                    // future edits can send a fresh BaseVersion.
                    if (result.NewVersion is long v)
                        await StampVersionAsync(db, entry.Entity, entry.EntityId, v, ct);
                    pushed++;
                    break;
                case MutationOutcome.Conflict:
                    entry.Status = OutboxStatus.Conflicted;
                    entry.ConflictServerPayloadJson = result.ServerPayloadJson;
                    entry.LastError = result.Error;
                    conflicts++;
                    break;
                case MutationOutcome.Rejected:
                    entry.Status = OutboxStatus.Failed;
                    entry.LastError = result.Error;
                    break;
            }
        }
        await db.SaveChangesAsync(ct);

        // Hand conflicts to the resolver; for this sketch we use server-wins.
        foreach (var e in batch.Where(e => e.Status == OutboxStatus.Conflicted))
            await ConflictResolver.ServerWinsAsync(db, e, ct);

        return (pushed, conflicts);
    }

    private static Task StampVersionAsync(LocalDbContext db, EntityKind kind, Guid id, long v, CancellationToken ct)
        => kind switch
        {
            EntityKind.Customer          => db.Customers.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v), ct),
            EntityKind.Technician        => db.Technicians.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v), ct),
            EntityKind.Part              => db.Parts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v), ct),
            EntityKind.WorkOrder         => db.WorkOrders.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v), ct),
            EntityKind.WorkOrderLineItem => db.LineItems.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, v), ct),
            _                            => Task.CompletedTask
        };

    // ---------------------------------------------------------------------------------
    // PULL
    // ---------------------------------------------------------------------------------
    private async Task<int> PullAsync(CancellationToken ct)
    {
        Raise(SyncState.Pulling);
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

        var cursor = await db.Cursors.FirstOrDefaultAsync(c => c.Id == 1, ct);
        if (cursor is null) { cursor = new SyncCursor { Id = 1 }; db.Cursors.Add(cursor); await db.SaveChangesAsync(ct); }

        var total = 0;
        while (true)
        {
            var req = new PullRequest(cursor.LastServerVersion, MaxBatchSize: 500);
            var resp = await _http.PostAsJsonAsync("/sync/pull", req, Json, ct);
            resp.EnsureSuccessStatusCode();
            var body = (await resp.Content.ReadFromJsonAsync<PullResponse>(Json, ct))!;

            if (body.Changes.Count == 0) break;

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var change in body.Changes)
            {
                await ApplyPulledChangeAsync(db, change, ct);
                if (change.Version > cursor.LastServerVersion)
                    cursor.LastServerVersion = change.Version;
            }
            cursor.LastPulledAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            total += body.Changes.Count;
            if (!body.HasMore) break;
        }
        return total;
    }

    private static async Task ApplyPulledChangeAsync(LocalDbContext db, EntityChange c, CancellationToken ct)
    {
        switch (c.Entity)
        {
            case EntityKind.Customer:          await Upsert<Customer>(db, db.Customers, c, ct); break;
            case EntityKind.Technician:        await Upsert<Technician>(db, db.Technicians, c, ct); break;
            case EntityKind.Part:              await Upsert<Part>(db, db.Parts, c, ct); break;
            case EntityKind.WorkOrder:         await Upsert<WorkOrder>(db, db.WorkOrders, c, ct); break;
            case EntityKind.WorkOrderLineItem: await Upsert<WorkOrderLineItem>(db, db.LineItems, c, ct); break;
        }
    }

    private static async Task Upsert<T>(LocalDbContext db, DbSet<T> set, EntityChange c, CancellationToken ct)
        where T : class, ISyncableEntity, new()
    {
        var existing = await set.FindAsync(new object?[] { c.EntityId }, ct);
        if (c.IsDeleted)
        {
            if (existing is not null) { existing.IsDeleted = true; existing.Version = c.Version; }
            return;
        }

        var incoming = JsonSerializer.Deserialize<T>(c.PayloadJson, Json)!;
        if (existing is null) set.Add(incoming);
        else db.Entry(existing).CurrentValues.SetValues(incoming);
    }

    private void Raise(SyncState s) => StateChanged?.Invoke(s);
}
