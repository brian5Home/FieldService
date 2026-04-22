using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Data.Migrations;

/// <summary>
/// Adds a Priority column (int, default 0 = Normal) to WorkOrders. This is the client-side
/// half of the feature flagged in docs/UPGRADES.md.
///
/// Important: after altering the column, we reset the sync cursor so the client re-pulls
/// every WorkOrder from the server and picks up the server-populated Priority values.
/// This is heavy-handed; a real migration should leave the cursor alone and rely on the
/// fact that the server bumped every row's Version when it added the column.
/// </summary>
public sealed class M002_AddPriorityToWorkOrder : ILocalMigration
{
    public int Version => 2;
    public string Description => "Add Priority column to WorkOrders";

    public async Task UpAsync(LocalDbContext db, CancellationToken ct)
    {
        // SQLite supports ALTER TABLE ADD COLUMN with a default. No data backfill needed
        // because the default itself is the desired value for locally-created rows that
        // predated the feature.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE WorkOrders ADD COLUMN Priority INTEGER NOT NULL DEFAULT 0;", ct);
    }
}
