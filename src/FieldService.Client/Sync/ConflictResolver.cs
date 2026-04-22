using System.Text.Json;
using FieldService.Client.Data;
using FieldService.Shared.Domain;
using FieldService.Shared.Sync;

namespace FieldService.Client.Sync;

/// <summary>
/// Conflict resolution policies. For this sketch we implement server-wins (the simplest thing
/// that doesn't lose data silently — the server's view is applied locally, and the user's
/// pending change remains in the outbox tagged Conflicted so the UI can surface it).
///
/// Real apps often want one of:
///  * Last-writer-wins by server-timestamp (what the server already does).
///  * Field-level merge: take server's status, keep the user's description text.
///  * Interactive: show both versions to the user and let them pick.
/// Swap the body of ResolveAsync for whichever policy fits the entity.
/// </summary>
public static class ConflictResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task ServerWinsAsync(LocalDbContext db, OutboxEntry entry, CancellationToken ct)
    {
        if (entry.ConflictServerPayloadJson is null) return;

        switch (entry.Entity)
        {
            case EntityKind.WorkOrder:
                await ApplyServerValue<WorkOrder>(db, db.WorkOrders, entry, ct); break;
            case EntityKind.WorkOrderLineItem:
                await ApplyServerValue<WorkOrderLineItem>(db, db.LineItems, entry, ct); break;
            case EntityKind.Customer:
                await ApplyServerValue<Customer>(db, db.Customers, entry, ct); break;
            case EntityKind.Part:
                await ApplyServerValue<Part>(db, db.Parts, entry, ct); break;
            case EntityKind.Technician:
                await ApplyServerValue<Technician>(db, db.Technicians, entry, ct); break;
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task ApplyServerValue<T>(LocalDbContext db,
                                                  Microsoft.EntityFrameworkCore.DbSet<T> set,
                                                  OutboxEntry entry, CancellationToken ct)
        where T : class, ISyncableEntity
    {
        var server = JsonSerializer.Deserialize<T>(entry.ConflictServerPayloadJson!, Json)!;
        var existing = await set.FindAsync(new object?[] { entry.EntityId }, ct);
        if (existing is null) set.Add(server);
        else db.Entry(existing).CurrentValues.SetValues(server);
    }
}
