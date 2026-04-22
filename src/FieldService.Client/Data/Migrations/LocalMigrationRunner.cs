using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FieldService.Client.Data.Migrations;

public sealed class LocalMigrationRunner
{
    private readonly LocalDbContext _db;
    private readonly IEnumerable<ILocalMigration> _migrations;
    private readonly ILogger<LocalMigrationRunner> _log;

    public LocalMigrationRunner(LocalDbContext db,
                                IEnumerable<ILocalMigration> migrations,
                                ILogger<LocalMigrationRunner> log)
    {
        _db = db;
        _migrations = migrations;
        _log = log;
    }

    /// <summary>
    /// Run on app startup, BEFORE any repository hits the DB. Opens the DB (creating it if
    /// absent), asks for the current schema version, and applies any pending migrations in
    /// ascending order. Each migration runs in its own transaction — if it throws we stop
    /// and surface the error; the UI should show "database upgrade failed, please reload"
    /// rather than letting the app start in an inconsistent state.
    /// </summary>
    public async Task EnsureUpgradedAsync(CancellationToken ct = default)
    {
        // EnsureCreatedAsync creates the DB with the LATEST EF model if no DB exists yet.
        // That's fine for first-install: we mark the schema version as the max applied.
        var newDb = !await _db.Database.CanConnectAsync(ct);
        await _db.Database.EnsureCreatedAsync(ct);

        var current = await GetCurrentVersionAsync(ct);
        var pending = _migrations.OrderBy(m => m.Version)
                                 .Where(m => m.Version > current)
                                 .ToList();

        if (newDb && pending.Count > 0)
        {
            // Fresh install: EnsureCreated already built the final schema. Stamp the version.
            var latest = pending.Max(m => m.Version);
            await SetCurrentVersionAsync(latest, ct);
            _log.LogInformation("Fresh local DB; stamped schema version {V}", latest);
            return;
        }

        foreach (var migration in pending)
        {
            _log.LogInformation("Applying local migration {V}: {D}",
                                migration.Version, migration.Description);
            await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                await migration.UpAsync(_db, ct);
                await SetCurrentVersionAsync(migration.Version, ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    private async Task<int> GetCurrentVersionAsync(CancellationToken ct)
    {
        var row = await _db.SchemaVersions.FirstOrDefaultAsync(v => v.Id == 1, ct);
        return row?.Version ?? 0;
    }

    private async Task SetCurrentVersionAsync(int version, CancellationToken ct)
    {
        var row = await _db.SchemaVersions.FirstOrDefaultAsync(v => v.Id == 1, ct);
        if (row is null)
        {
            _db.SchemaVersions.Add(new ClientSchemaVersion
            {
                Id = 1, Version = version, AppliedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            row.Version = version;
            row.AppliedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }
}
