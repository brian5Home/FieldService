using Microsoft.EntityFrameworkCore;

namespace FieldService.Client.Data.Migrations;

/// <summary>
/// No-op migration: on a fresh install EF Core's EnsureCreated builds the schema from the
/// model snapshot, and the runner stamps the version. This migration exists so future
/// installations that start from v0 pass through the same gate on their way to vN.
/// </summary>
public sealed class M001_InitialSchema : ILocalMigration
{
    public int Version => 1;
    public string Description => "Initial schema";
    public Task UpAsync(LocalDbContext db, CancellationToken ct) => Task.CompletedTask;
}
