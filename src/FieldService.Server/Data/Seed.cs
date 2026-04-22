using FieldService.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Server.Data;

/// <summary>
/// Idempotent seed. Runs on startup; inserts reference data only if the tables are empty.
/// Each row is allocated a Version from SyncSequence, which means the very first /sync/pull
/// from a fresh client returns everything in one batch.
/// </summary>
public static class Seed
{
    public static async Task RunAsync(AppDbContext db, CancellationToken ct = default)
    {
        // Use migrations when present; otherwise create schema from the model.
        // This keeps first-run container startup working even before EF migrations are added.
        if (db.Database.GetMigrations().Any())
            await db.Database.MigrateAsync(ct);
        else
            await db.Database.EnsureCreatedAsync(ct);

        // Recover from a half-initialized SQLite file (e.g., created before schema existed).
        if (!await TableExistsAsync(db, "Customers", ct))
        {
            await db.Database.EnsureDeletedAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
        }

        if (await db.Customers.AnyAsync(ct)) return;

        var now = DateTime.UtcNow;

        // Allocate a contiguous band of versions for the whole seed set.
        // This keeps Version strictly ascending with insertion order.
        const int seedCount = 18;
        var v = await db.AllocateVersionsAsync(seedCount, ct);

        var techA = new Technician
        {
            Name = "Ava Chen", Email = "ava@fs.example", Phone = "555-0101",
            SkillCodes = "HVAC,ELEC", IsActive = true,
            Version = v++, UpdatedAt = now
        };
        var techB = new Technician
        {
            Name = "Marcus Rivera", Email = "marcus@fs.example", Phone = "555-0102",
            SkillCodes = "PLUMB,HVAC", IsActive = true,
            Version = v++, UpdatedAt = now
        };
        db.Technicians.AddRange(techA, techB);

        var cust1 = new Customer
        {
            Name = "Northgate Apartments", Phone = "555-1000",
            Email = "ops@northgate.example",
            AddressLine1 = "220 Elm Street", City = "Bellevue", Region = "WA",
            PostalCode = "98004", Tier = CustomerTier.Preferred,
            Version = v++, UpdatedAt = now
        };
        var cust2 = new Customer
        {
            Name = "Harborview Dental", Phone = "555-1001",
            Email = "facilities@harborview.example",
            AddressLine1 = "41 Pike Place", City = "Seattle", Region = "WA",
            PostalCode = "98101", Tier = CustomerTier.Vip,
            Version = v++, UpdatedAt = now
        };
        db.Customers.AddRange(cust1, cust2);

        var p1 = new Part { Sku = "HVAC-FLT-20x25", Name = "20x25 HVAC Filter", Category = "HVAC",
            UnitPrice = 14.50m, StockOnHand = 48, Version = v++, UpdatedAt = now };
        var p2 = new Part { Sku = "CAP-35-440",    Name = "35/5 MFD Run Capacitor", Category = "HVAC",
            UnitPrice = 22.00m, StockOnHand = 25, Version = v++, UpdatedAt = now };
        var p3 = new Part { Sku = "THERM-T6-PRO",  Name = "Honeywell T6 Thermostat", Category = "HVAC",
            UnitPrice = 89.00m, StockOnHand = 12, Version = v++, UpdatedAt = now };
        var p4 = new Part { Sku = "WIRE-14-2-ROM", Name = "14/2 Romex 50ft", Category = "ELEC",
            UnitPrice = 42.00m, StockOnHand = 30, Version = v++, UpdatedAt = now };
        db.Parts.AddRange(p1, p2, p3, p4);

        var wo1 = new WorkOrder
        {
            Number = "WO-2026-00001",
            CustomerId = cust1.Id,
            AssignedTechnicianId = techA.Id,
            Title = "Rooftop unit — not cooling",
            Description = "Building C, unit 4. Customer reports warm air since Monday.",
            Status = WorkOrderStatus.Scheduled,
            ScheduledFor = now.AddDays(1),
            Version = v++, UpdatedAt = now
        };
        var wo1Items = new[]
        {
            new WorkOrderLineItem { WorkOrderId = wo1.Id, Kind = LineItemKind.Labor,
                Description = "Diagnostic + repair (est.)", Quantity = 2.0m, UnitPrice = 120m,
                Version = v++, UpdatedAt = now },
            new WorkOrderLineItem { WorkOrderId = wo1.Id, Kind = LineItemKind.Part,
                PartId = p2.Id, Description = p2.Name, Quantity = 1, UnitPrice = p2.UnitPrice,
                StockDelta = -1, Version = v++, UpdatedAt = now },
        };
        wo1.TotalAmount = wo1Items.Sum(i => i.LineTotal);
        db.WorkOrders.Add(wo1);
        db.LineItems.AddRange(wo1Items);

        var wo2 = new WorkOrder
        {
            Number = "WO-2026-00002",
            CustomerId = cust2.Id,
            AssignedTechnicianId = techB.Id,
            Title = "Quarterly PM — all units",
            Description = "Preventive maintenance across 3 rooftop units.",
            Status = WorkOrderStatus.Draft,
            ScheduledFor = now.AddDays(3),
            Version = v++, UpdatedAt = now
        };
        var wo2Items = new[]
        {
            new WorkOrderLineItem { WorkOrderId = wo2.Id, Kind = LineItemKind.Labor,
                Description = "PM labor (3 units)", Quantity = 4.5m, UnitPrice = 120m,
                Version = v++, UpdatedAt = now },
            new WorkOrderLineItem { WorkOrderId = wo2.Id, Kind = LineItemKind.Part,
                PartId = p1.Id, Description = p1.Name, Quantity = 6, UnitPrice = p1.UnitPrice,
                StockDelta = -6, Version = v++, UpdatedAt = now },
        };
        wo2.TotalAmount = wo2Items.Sum(i => i.LineTotal);
        db.WorkOrders.Add(wo2);
        db.LineItems.AddRange(wo2Items);

        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(AppDbContext db, string tableName, CancellationToken ct)
    {
        await using var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1";

        var p = cmd.CreateParameter();
        p.ParameterName = "$name";
        p.Value = tableName;
        cmd.Parameters.Add(p);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }
}
