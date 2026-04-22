using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Data.Migrations;

/// <summary>
/// A single forward-only schema migration run against the browser-hosted SQLite database.
/// Version numbers MUST be monotonically increasing and must never be reused even if a
/// migration is deleted — bump the next one instead, as in Rails/EF.
/// </summary>
public interface ILocalMigration
{
    int Version { get; }
    string Description { get; }
    Task UpAsync(LocalDbContext db, CancellationToken ct);
}
