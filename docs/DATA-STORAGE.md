# How data is stored on the user's device

This document explains where and how the FieldService PWA persists data locally in the
browser, how that data syncs to the server, and how this compares to alternatives like
PowerSync.

---

## Storage technology: SQLite-WASM in OPFS

The app uses **SQLite compiled to WebAssembly**, running entirely inside the browser. The
database file (`fieldservice.db`) is stored in the browser's **Origin Private File System
(OPFS)**.

### What is OPFS?

OPFS is a modern browser API (supported in Chrome 86+, Edge 86+, Firefox 111+, Safari
15.2+) that gives each origin a private, sandboxed file system. Key properties:

| Property | Detail |
|----------|--------|
| **Visibility** | Invisible to the user; not in Downloads, Desktop, or any user-facing folder |
| **Persistence** | Survives page reloads, tab closes, and browser restarts |
| **Scope** | Isolated per origin (`https://yourapp.example.com`) |
| **Quota** | Governed by the browser's storage quota (typically a percentage of available disk) |
| **Eviction** | Can be evicted under extreme storage pressure unless the site uses the `navigator.storage.persist()` API |
| **Access** | Only accessible via JavaScript APIs; no direct filesystem path on disk |
| **Performance** | Supports synchronous access in Web Workers via `createSyncAccessHandle()`, which SQLite-WASM uses for near-native read/write speed |

### Where does the file physically live on disk?

Although OPFS has no user-visible path, browsers store it internally:

| Browser | Typical internal location |
|---------|--------------------------|
| Chrome/Edge | `%LocalAppData%\Google\Chrome\User Data\Default\File System\` (or Edge equivalent) inside an opaque directory structure |
| Firefox | `%AppData%\Mozilla\Firefox\Profiles\<profile>\storage\default\<origin>\` |
| Safari | Managed within WebKit's internal storage directories |

Users and developers should **not** rely on or manipulate these paths directly. Use browser
DevTools (Application > Storage > OPFS) to inspect the contents.

### Initialization

```csharp
// Program.cs
Batteries_V2.Init();  // Binds the SQLite native provider for WASM

builder.Services.AddDbContextFactory<LocalDbContext>(o =>
    o.UseSqlite("Data Source=fieldservice.db"));
```

`Batteries_V2.Init()` is required to wire up the `e_sqlite3` native library compiled to
WASM. EF Core then creates and manages `fieldservice.db` inside OPFS automatically.

---

## What is stored locally

### Domain tables

All five domain entities are stored in SQLite and share sync metadata via `ISyncableEntity`:

```csharp
public interface ISyncableEntity
{
    Guid   Id        { get; set; }  // Primary key
    long   Version   { get; set; }  // Server-assigned monotonic sequence number
    DateTime UpdatedAt { get; set; }  // Wall-clock timestamp (informational)
    bool   IsDeleted { get; set; }  // Soft-delete tombstone
}
```

| Table | Key fields | Notes |
|-------|-----------|-------|
| **Customers** | Name, Phone, Email, Address, Tier, Notes | Contact/account records |
| **Technicians** | Name, Email, Phone, SkillCodes, IsActive | Field personnel |
| **Parts** | SKU (unique), Name, Category, UnitPrice, StockOnHand, IsActive | StockOnHand is convergent: technicians deduct offline, server aggregates deltas |
| **WorkOrders** | Number, CustomerId, TechnicianId, Title, Status, ScheduledFor, Priority | Central work item; has child LineItems |
| **WorkOrderLineItems** | WorkOrderId, Kind, PartId, Description, Quantity, UnitPrice, StockDelta | Parts/labor/adjustments attached to a work order |

### Sync infrastructure tables

| Table | Purpose |
|-------|---------|
| **OutboxEntries** | Transactional outbox. Every local mutation is written atomically alongside the domain change. The sync worker drains this to the server. |
| **SyncCursors** | Singleton row tracking the last server `Version` pulled, plus timestamps of last pull/push. |
| **ClientSchemaVersions** | Singleton row tracking which client-side migration has been applied. |

### No other storage mechanisms

The app does **not** use `localStorage`, `sessionStorage`, `IndexedDB`, or the Cache API
for application data. The only non-SQLite storage is the **service worker** precache, which
caches the app shell (Blazor runtime, framework DLLs, static assets) so the app loads
offline.

---

## The transactional outbox pattern

Every mutation the user makes goes through a repository that wraps the domain change and an
outbox entry in a single SQLite transaction:

```
User taps "Complete Work Order"
    └─► Repository.UpdateStatusAsync()
            ├─ UPDATE WorkOrders SET Status = 'Completed' ...
            ├─ INSERT INTO OutboxEntries (ClientMutationId, Entity, EntityId, Kind, PayloadJson, ...)
            └─ COMMIT
```

This guarantees:
- No UI change exists without the sync service knowing about it
- No orphan outbox entry exists for a domain row that was rolled back
- The user sees the change immediately, regardless of network state

### Outbox entry structure

| Field | Purpose |
|-------|---------|
| `ClientMutationId` | GUID used as an idempotency key on the server |
| `Entity` / `EntityId` | Which table and row this mutation targets |
| `Kind` | Insert, Update, or Delete |
| `BaseVersion` | The server Version the client had when it made the change (optimistic concurrency) |
| `PayloadJson` | Full serialized entity snapshot |
| `Status` | Pending -> Sending -> Confirmed / Conflicted / Failed |
| `ConflictServerPayloadJson` | Populated when the server returns a conflict |

---

## Sync lifecycle

```
App boot
  │
  ├─ LocalMigrationRunner applies any pending client schema migrations
  │
  └─ SyncService starts (runs every 60 seconds, also reacts to online/offline events)
       │
       ├─ PUSH: Batch up to 50 pending outbox entries → POST /sync/push
       │    └─ Server returns per-mutation outcomes (Applied, Duplicate, Conflict, Rejected)
       │
       └─ PULL: Loop GET /sync/pull?since={cursor} (batch size 500)
            └─ Upsert received entities, advance cursor, handle soft-delete tombstones
```

### Conflict resolution

The current implementation uses **server-wins**: when the server detects a version mismatch
(client's `BaseVersion` < server's current version), it rejects the mutation with a
`Conflict` outcome and sends back the server's current payload. The client applies the
server's version locally and marks the outbox entry as `Conflicted` so the UI can surface
it.

---

## Comparison: this approach vs. PowerSync

[PowerSync](https://www.powersync.com/) is a commercial sync layer that pairs a client-side
SQLite database with a server-side sync service. Here is how the two approaches compare:

| Dimension | FieldService (hand-rolled) | PowerSync |
|-----------|---------------------------|-----------|
| **Client database** | SQLite-WASM in OPFS (same) | SQLite-WASM in OPFS (same) |
| **Sync engine** | Custom outbox + version-cursor pull (~260 LoC) | Managed sync service (hosted or self-hosted) |
| **Conflict model** | Explicit: server-wins with outbox status tracking, pluggable | Configurable via "sync rules" and client-side conflict handlers |
| **Backend integration** | Direct: sync endpoints are part of the ASP.NET Core API | Requires a PowerSync service between your backend and clients; backend writes to Postgres, PowerSync reads the WAL |
| **Backend database** | Any (SQL Server, Postgres, SQLite) via EF Core | Postgres, MongoDB, MySQL, or Supabase (PowerSync reads the replication stream) |
| **Schema definition** | C# domain classes shared between client and server | YAML "sync rules" define which tables/columns sync to which users |
| **Offline writes** | Transactional outbox with idempotency keys | Client-side CRUD queue with upload handlers you implement |
| **Protocol versioning** | Built-in (`X-FieldService-Protocol` header, 426 on mismatch) | Handled by PowerSync SDK versioning |
| **Cost** | Zero (code you own) | Free tier available; paid plans for production workloads |
| **Operational overhead** | You maintain the sync endpoints and conflict logic | PowerSync service must be deployed and monitored |
| **Transparency** | Full: protocol is ~60 LoC of DTOs, sync worker is ~200 LoC | SDK is open-source, but the sync service is a black box unless self-hosted |

### When would PowerSync be a better fit?

- You want sync-as-a-service and don't want to write/maintain sync endpoints
- Your backend is Postgres and you want automatic change detection via WAL
- You need partial sync (different users see different subsets of data) defined declaratively
- You want built-in support for multiple client platforms (Flutter, React Native, Web)

### Why this project chose hand-rolled sync

From the README: *"We deliberately hand-roll sync instead of adopting DotMim.Sync so the
protocol is legible and conflict semantics are explicit."*

The same reasoning applies to PowerSync. The hand-rolled approach gives:
- Full visibility into every byte that crosses the wire
- Conflict semantics that match the domain (e.g., convergent stock quantities for Parts)
- No external service dependency; the sync endpoints are just three Minimal API routes
- Freedom to use any backend database, not just Postgres

---

## Data lifecycle summary

```
┌─────────────────────────────────────────────────────────────────┐
│                     User's Browser                              │
│                                                                 │
│   ┌──────────────────────────────────────────────────────────┐  │
│   │  OPFS (Origin Private File System)                       │  │
│   │  └── fieldservice.db  (SQLite-WASM)                      │  │
│   │       ├── Customers, Technicians, Parts,                 │  │
│   │       │   WorkOrders, WorkOrderLineItems                 │  │
│   │       ├── OutboxEntries  (pending mutations)             │  │
│   │       ├── SyncCursors    (last server version pulled)    │  │
│   │       └── ClientSchemaVersions (migration tracker)       │  │
│   └──────────────────────────────────────────────────────────┘  │
│                                                                 │
│   ┌──────────────────────────────────────────────────────────┐  │
│   │  Service Worker Cache (Cache API)                        │  │
│   │  └── App shell: Blazor runtime, DLLs, static assets     │  │
│   │      (NOT application data)                              │  │
│   └──────────────────────────────────────────────────────────┘  │
│                                                                 │
│   localStorage / sessionStorage / IndexedDB: NOT USED          │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Clearing data

Users can clear the local database by:
1. **Browser DevTools** > Application > Storage > Clear site data
2. **Browser settings** > Clear browsing data (if "Cookies and other site data" is checked)
3. The browser may evict OPFS data under extreme storage pressure (mitigated by requesting
   persistent storage via `navigator.storage.persist()`)

On next app load after clearing, the migration runner recreates the schema and the sync
service pulls a full dataset from the server.
