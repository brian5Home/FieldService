# Worked example: adding `Priority` to `WorkOrder`

This doc walks through the full mechanics of a schema change that touches server + client + sync
protocol. The scenario: product wants technicians to see a priority flag (Low / Normal / High /
Urgent) on every work order, filter by it, and have the server emit a priority-based alert.

## 1. Classify the change

Every change falls into one of three buckets. This one is in bucket (b).

| Bucket | Example | Protocol bump? | Breaks old clients? |
|--------|---------|----------------|---------------------|
| (a) Additive, tolerant | New nullable column, optional field in payload | No | No |
| (b) Additive, required | New required field with a server default, new enum value | Usually no | Only if validation rejects old payloads |
| (c) Breaking | Renamed/removed column, changed semantics, required behavior | **Yes** | Yes |

For (b) the server must:
- accept push payloads from old clients that omit the field (apply default server-side)
- include the field in pull payloads; old clients will ignore unknown JSON properties by default

## 2. Server side — EF Core migration + seed backfill

Add the field to the shared domain:

```csharp
// FieldService.Shared/Domain/WorkOrder.cs
public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;

public enum WorkOrderPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }
```

Generate the EF Core migration:

```bash
dotnet ef migrations add AddWorkOrderPriority --project src/FieldService.Server
```

Patch the generated `Up()` to backfill AND to bump every work order's `Version` so clients
re-pull them:

```csharp
protected override void Up(MigrationBuilder mb)
{
    mb.AddColumn<int>(
        name: "Priority",
        table: "WorkOrders",
        nullable: false,
        defaultValue: 1); // Normal

    // Bump Version on every row so pulls emit them to clients. This is critical: without it,
    // a client with cursor = X will never learn Priority exists on pre-X rows.
    mb.Sql(@"
        UPDATE s SET NextVersion = NextVersion + (SELECT COUNT(*) FROM WorkOrders)
        FROM SyncSequence s WHERE s.Id = 1;
    ");
    mb.Sql(@"
        UPDATE w
          SET w.Version = s.NextVersion - row_number() over (order by w.Id)
          FROM WorkOrders w, SyncSequence s;
    ");
}
```

(Exact SQL depends on your DB; in SQLite you'd do this in two statements with a temp table.)

Apply the migration on deploy; `Seed.RunAsync` is idempotent and won't re-insert rows.

## 3. Sync protocol version

If the change were breaking, we'd bump `SyncProtocol.Current` from 2 to 3 and set
`MinimumSupported = 3`. Old clients would then get `426 Upgrade Required` on every sync call
and show the "update available" banner.

Because this change is additive-tolerant, we leave `Current = 2` and add a note in the
CHANGELOG. Old clients keep working; they just don't see the new field.

## 4. Client side — migration file

Add `M003_AddPriorityToWorkOrder`:

```csharp
public sealed class M003_AddPriorityToWorkOrder : ILocalMigration
{
    public int Version => 3;
    public string Description => "Add Priority column to WorkOrders";
    public async Task UpAsync(LocalDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE WorkOrders ADD COLUMN Priority INTEGER NOT NULL DEFAULT 1;", ct);
    }
}
```

Register it in `Program.cs`:

```csharp
builder.Services.AddSingleton<ILocalMigration, M003_AddPriorityToWorkOrder>();
```

## 5. Ship order

This sequence avoids the window where an old client sees a new server but the server expects
payloads it can't parse:

1. **Deploy the server first.** New column exists, default is applied, old clients continue
   to push payloads without `Priority` — the server accepts because the field has a default.
2. **Ship the client.** Users open the app; service worker picks up the new bundle on next
   cold start (or sooner, if you force an update). On boot, `LocalMigrationRunner` runs M003
   before the first query touches `WorkOrders`. The next pull receives every work order again
   (Version was bumped) and the client stores the new `Priority` values.
3. **Turn on the feature flag.** The UI that displays/filters priority can be rolled out
   behind a flag so the new code reaches everyone before any user sees an incomplete
   experience.

## 6. What would make this a protocol-breaking change

Imagine product instead wanted to split `WorkOrder.Title` into `Summary` + `SymptomCode`, and
remove `Title`. Old clients would:
- Send `Title` in push payloads → server ignores, data loss
- Receive payloads without `Title` → old UI shows empty strings

That's bucket (c). Procedure:
1. Bump `SyncProtocol.Current` = 3, `MinimumSupported` = 3.
2. Ship a transition release (protocol 2 → 3) where the server accepts BOTH shapes and
   translates. `MinimumSupported` stays at 2 for this release.
3. Wait until your telemetry shows no clients below protocol 3 syncing.
4. Ship a cleanup release that sets `MinimumSupported = 3` and drops the translation layer.

The two-release dance is the price of never letting a client lose data because of a schema
change it didn't know about.

## 7. Quick sanity checklist for any schema change

- Will the server migration change any existing row's Version? If not, clients won't pull.
- Does the push handler accept payloads from clients that predate the change?
- Does the pull payload shape make sense to a client that doesn't know the new field yet?
- Is there a client migration whose Version is strictly greater than any previous migration?
- Has `SyncProtocol.Current` been bumped if the change is breaking?
- Is there a feature flag so the UI can be rolled back without rolling back the schema?
