using FieldService.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Data;

/// <summary>
/// Client-side EF Core over SQLite-WASM. The backing file lives in the browser's OPFS (Origin
/// Private File System) so it survives reloads; Program.cs points the connection string there.
///
/// NOTE: unlike the server, the client does NOT assign Version numbers. It leaves Version = 0
/// on newly-created rows; the sync service fills it in with whatever the server returns when
/// the mutation is accepted.
/// </summary>
public sealed class LocalDbContext : DbContext
{
    public LocalDbContext(DbContextOptions<LocalDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderLineItem> LineItems => Set<WorkOrderLineItem>();

    public DbSet<OutboxEntry> Outbox => Set<OutboxEntry>();
    public DbSet<SyncCursor> Cursors => Set<SyncCursor>();
    public DbSet<ClientSchemaVersion> SchemaVersions => Set<ClientSchemaVersion>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasKey(x => x.Id);
        b.Entity<Technician>().HasKey(x => x.Id);
        b.Entity<Part>().HasKey(x => x.Id);

        b.Entity<WorkOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AssignedTechnicianId);
            e.HasMany(x => x.LineItems).WithOne().HasForeignKey(x => x.WorkOrderId);
        });

        b.Entity<WorkOrderLineItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.WorkOrderId);
            e.Ignore(x => x.LineTotal);
        });

        b.Entity<OutboxEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        b.Entity<SyncCursor>().HasKey(x => x.Id);
        b.Entity<ClientSchemaVersion>().HasKey(x => x.Id);
    }
}

/// <summary>
/// Singleton row: the last server Version the client has pulled. Next pull sends
/// <c>{ SinceVersion = LastServerVersion }</c>.
/// </summary>
public sealed class SyncCursor
{
    public int Id { get; set; } = 1;
    public long LastServerVersion { get; set; }
    public DateTime? LastPulledAtUtc { get; set; }
    public DateTime? LastPushedAtUtc { get; set; }
}

public sealed class ClientSchemaVersion
{
    public int Id { get; set; } = 1;
    public int Version { get; set; }
    public DateTime AppliedAtUtc { get; set; }
}
