using System.Text.Json;
using FieldService.Shared.Domain;
using FieldService.Shared.Sync;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Data;

/// <summary>
/// The only place the UI talks to the local DB. Every mutating method writes BOTH the domain
/// row AND an OutboxEntry in a single EF Core transaction. This is the transactional outbox
/// pattern — it guarantees we can never have a UI change that the sync service doesn't know
/// about, or an outbox entry whose domain row disappeared.
/// </summary>
public sealed class WorkOrderRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LocalDbContext _db;

    public WorkOrderRepository(LocalDbContext db) => _db = db;

    public Task<List<WorkOrder>> ListForTechnicianAsync(Guid techId, CancellationToken ct) =>
        _db.WorkOrders.AsNoTracking()
                      .Where(w => !w.IsDeleted && w.AssignedTechnicianId == techId)
                      .Include(w => w.LineItems)
                      .OrderBy(w => w.ScheduledFor)
                      .ToListAsync(ct);

    public async Task<WorkOrder> CreateAsync(WorkOrder draft, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (draft.Id == Guid.Empty) draft.Id = Guid.NewGuid();
        draft.UpdatedAt = DateTime.UtcNow;
        draft.Version = 0; // server will assign
        _db.WorkOrders.Add(draft);

        _db.Outbox.Add(new OutboxEntry
        {
            ClientMutationId = Guid.NewGuid(),
            Entity = EntityKind.WorkOrder,
            EntityId = draft.Id,
            Kind = MutationKind.Insert,
            BaseVersion = null,
            PayloadJson = JsonSerializer.Serialize(draft, Json),
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return draft;
    }

    public async Task UpdateStatusAsync(Guid workOrderId, WorkOrderStatus status, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var wo = await _db.WorkOrders.FirstAsync(w => w.Id == workOrderId, ct);

        wo.Status = status;
        if (status == WorkOrderStatus.OnSite) wo.ArrivedAt ??= DateTime.UtcNow;
        if (status == WorkOrderStatus.Completed) wo.CompletedAt ??= DateTime.UtcNow;
        wo.UpdatedAt = DateTime.UtcNow;

        _db.Outbox.Add(new OutboxEntry
        {
            ClientMutationId = Guid.NewGuid(),
            Entity = EntityKind.WorkOrder,
            EntityId = wo.Id,
            Kind = MutationKind.Update,
            BaseVersion = wo.Version,      // the server will reject if stale
            PayloadJson = JsonSerializer.Serialize(wo, Json),
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Add a part line. Decrements local stock and carries a StockDelta for the server.</summary>
    public async Task AddPartLineAsync(Guid workOrderId, Guid partId, decimal quantity, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var part = await _db.Parts.FirstAsync(p => p.Id == partId, ct);

        var line = new WorkOrderLineItem
        {
            WorkOrderId = workOrderId,
            Kind = LineItemKind.Part,
            PartId = partId,
            Description = part.Name,
            Quantity = quantity,
            UnitPrice = part.UnitPrice,
            StockDelta = -(int)quantity,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.LineItems.Add(line);

        // Local stock mirrors what the server will do on accept.
        part.StockOnHand -= (int)quantity;
        part.UpdatedAt = DateTime.UtcNow;

        _db.Outbox.Add(new OutboxEntry
        {
            ClientMutationId = Guid.NewGuid(),
            Entity = EntityKind.WorkOrderLineItem,
            EntityId = line.Id,
            Kind = MutationKind.Insert,
            BaseVersion = null,
            PayloadJson = JsonSerializer.Serialize(line, Json),
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
