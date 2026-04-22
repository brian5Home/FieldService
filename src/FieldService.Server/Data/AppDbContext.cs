using FieldService.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldService.Server.Data;

/// <summary>
/// Server-side EF Core context. Every syncable entity picks up its Version from a single
/// monotonic sequence (<see cref="SyncSequence"/>) in SaveChangesAsync, so every write anywhere
/// in the database gets a globally-ordered version number. The client uses this number as its
/// pull cursor.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderLineItem> LineItems => Set<WorkOrderLineItem>();
    public DbSet<SyncSequence> SyncSequence => Set<SyncSequence>();
    public DbSet<ProcessedMutation> ProcessedMutations => Set<ProcessedMutation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Version);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<Technician>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<Part>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Sku).IsUnique();
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
        });

        b.Entity<WorkOrder>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.AssignedTechnicianId);
            e.Property(x => x.TotalAmount).HasPrecision(18, 2);
            e.HasMany(x => x.LineItems).WithOne().HasForeignKey(x => x.WorkOrderId);
        });

        b.Entity<WorkOrderLineItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Version);
            e.HasIndex(x => x.WorkOrderId);
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Ignore(x => x.LineTotal); // computed in-memory
        });

        b.Entity<SyncSequence>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasData(new SyncSequence { Id = 1, NextVersion = 1 });
        });

        b.Entity<ProcessedMutation>(e =>
        {
            e.HasKey(x => x.ClientMutationId);
            e.HasIndex(x => x.ProcessedAtUtc);
        });
    }

    /// <summary>
    /// Allocate the next N version numbers atomically. Uses a row-level update so the caller
    /// holds no long-running lock; transactions around SaveChanges still guarantee isolation.
    /// </summary>
    public async Task<long> AllocateVersionsAsync(int count, CancellationToken ct = default)
    {
        var seq = await SyncSequence.SingleAsync(s => s.Id == 1, ct);
        var first = seq.NextVersion;
        seq.NextVersion += count;
        await SaveChangesAsync(ct);
        return first;
    }
}

/// <summary>Singleton row producing the monotonic Version sequence.</summary>
public sealed class SyncSequence
{
    public int Id { get; set; } = 1;
    public long NextVersion { get; set; } = 1;
}

/// <summary>
/// Idempotency ledger. When a mutation arrives we first check whether we've seen its
/// ClientMutationId; if so we short-circuit to <c>Duplicate</c> and return the cached
/// outcome so retries are safe.
/// </summary>
public sealed class ProcessedMutation
{
    public Guid ClientMutationId { get; set; }
    public long ResultingVersion { get; set; }
    public DateTime ProcessedAtUtc { get; set; }
}
