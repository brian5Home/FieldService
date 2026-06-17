# Performance and storage concerns

This document catalogues the performance characteristics and storage constraints of the
FieldService PWA's local data layer: SQLite-WASM in OPFS, the sync protocol, and query
patterns. Each section identifies current behavior, where bottlenecks will appear at scale,
and what mitigations exist.

---

## 1. OPFS storage limits

### Browser quotas

OPFS is governed by the same storage quota that applies to IndexedDB, Cache API, and other
origin-scoped storage. The quota varies by browser:

| Browser | Default quota | Notes |
|---------|---------------|-------|
| Chrome / Edge | Up to 60 % of total disk per origin; at least 10 GB on most machines | Eviction only under extreme global pressure and only for non-persistent origins |
| Firefox | Up to 50 % of disk (or 10 GB, whichever is smaller) per group of origins | Same eTLD+1 origins share a group quota |
| Safari | ~1 GB initial; user prompted for more | More aggressive eviction; 7-day cap on data from sites without recent interaction |

### Eviction risk

If the origin's storage is not marked **persistent**, the browser may evict it under
storage pressure (disk nearly full). The app does not currently call
`navigator.storage.persist()`. This means:

- Chrome: low risk (Chrome almost never evicts unless the user hasn't visited in a long
  time and disk is critically low)
- Safari: higher risk, especially on iOS where WebKit enforces the 7-day eviction policy
  for sites the user hasn't interacted with

**Mitigation:** Call `navigator.storage.persist()` on first load and surface the result
in the UI. If the browser denies persistent storage, warn the user that data may be cleared
if the device runs low on disk space.

### Estimating database size

The SQLite file on OPFS is a single `fieldservice.db`. Rough per-row sizes after SQLite
page overhead:

| Table | Est. row size | Notes |
|-------|---------------|-------|
| Customer | ~400–600 B | Address fields, notes |
| Technician | ~200 B | SkillCodes is a comma-separated string |
| Part | ~200 B | |
| WorkOrder | ~300 B | SignatureUrl could be a long string |
| WorkOrderLineItem | ~250 B | Description, prices |
| OutboxEntry | ~600–1200 B | PayloadJson is a full entity snapshot |
| SyncCursor | ~50 B | Singleton row |
| ClientSchemaVersion | ~30 B | Singleton row |

**Example projections** (domain rows only, excluding outbox):

| Scale | Rows | Estimated DB size |
|-------|------|-------------------|
| Small (1 tech, 6 months) | ~500 WOs, ~2 000 lines, ~50 customers, ~100 parts | ~1–2 MB |
| Medium (10 techs, 1 year) | ~5 000 WOs, ~20 000 lines, ~500 customers, ~500 parts | ~10–15 MB |
| Large (50 techs, 2 years) | ~25 000 WOs, ~100 000 lines, ~2 000 customers, ~1 000 parts | ~50–80 MB |

SQLite handles databases of this size without issue, even on constrained devices. The OPFS
quota (multiple GB) is unlikely to be a limiting factor for the domain data itself.

### What can bloat the database

1. **Outbox entries that are never cleaned up.** Confirmed and Failed entries accumulate
   forever. Each carries a full JSON payload (600–1 200 bytes). At 100 mutations/day, that's
   ~40 MB/year of dead outbox rows.
   - **Current state:** No outbox cleanup code exists.
   - **Recommendation:** Periodically delete Confirmed entries older than N days. Keep
     Conflicted/Failed entries for user review, then prune after resolution.

2. **Soft-delete tombstones.** `IsDeleted = true` rows remain in every table indefinitely.
   They consume space and slow down queries that forget to filter `!IsDeleted`.
   - **Recommendation:** After a tombstone has been synced to the server and enough time
     has passed for all clients to pull it, hard-delete it locally.

3. **SQLite WAL/journal files.** SQLite's default journal mode (DELETE) creates a `-journal`
   file during writes, then deletes it. WAL mode would leave a `-wal` file that can grow.
   Neither is configured explicitly; the default is fine for typical workloads but worth
   knowing about if debugging disk usage.

---

## 2. Search and query performance

### Current indexes (client-side)

| Table | Indexed columns | Covers which queries |
|-------|-----------------|---------------------|
| WorkOrders | `AssignedTechnicianId` | `ListForTechnicianAsync` filter |
| WorkOrderLineItems | `WorkOrderId` | `Include(w => w.LineItems)` join |
| OutboxEntries | `Status` | Push batch query (`WHERE Status IN (Pending, Sending)`) |
| OutboxEntries | `CreatedAtUtc` | Not currently queried; useful for cleanup |

### Missing indexes

| Table | Column | Why it matters |
|-------|--------|----------------|
| Customers | *(none beyond PK)* | Any search/filter on Name, Phone, or Email is a full table scan |
| Technicians | *(none beyond PK)* | Same for Name or Email lookups |
| Parts | *(none beyond PK)* | SKU lookup, Name search, or Category filter is a full scan |
| WorkOrders | `Status` | Filtering "open work orders" scans all rows |
| WorkOrders | `ScheduledFor` | `OrderBy(w => w.ScheduledFor)` sorts in memory after filtering |
| WorkOrders | `Number` | Looking up a work order by its human-readable number is a full scan |

At small scale (hundreds of rows) this is fine. At thousands of rows these scans become
noticeable on low-end devices (budget Android tablets, older iPads). Adding an index on
`Status` and a composite index on `(AssignedTechnicianId, ScheduledFor)` would cover the
main query path.

### No pagination in the UI

`ListForTechnicianAsync` loads **all** non-deleted work orders for a technician into memory
at once, including all line items via `Include()`:

```csharp
_db.WorkOrders.AsNoTracking()
    .Where(w => !w.IsDeleted && w.AssignedTechnicianId == techId)
    .Include(w => w.LineItems)
    .OrderBy(w => w.ScheduledFor)
    .ToListAsync(ct);
```

If a technician accumulates 2 000 work orders with an average of 4 line items each, this
query materializes ~10 000 objects. In Blazor WASM (single-threaded, no JIT), this can
cause a multi-second pause and significant memory pressure.

**Recommendations:**
- Filter by a date window or status (e.g., only open/scheduled work orders).
- Use `.Take(N)` for initial load with a "load more" pattern.
- Avoid `Include()` on list views; load line items on demand when the user opens a work
  order.

### Full-text search

There is no full-text search capability. SQLite supports FTS5, but EF Core doesn't expose
it. If users need to search customers by name or parts by description, the options are:

1. **LIKE queries** — simple but slow on large tables, especially with leading wildcards.
2. **FTS5 via raw SQL** — fast, but requires creating and maintaining a virtual table
   alongside the EF Core model. Client-side migrations can set this up.
3. **In-memory filtering** — load a small set, filter in C#. Only viable for small
   datasets.

---

## 3. Sync performance

### Pull performance

| Factor | Current behavior | Concern at scale |
|--------|-----------------|------------------|
| Batch size | 500 entities per pull request (server clamps 50–1 000) | Fine |
| Server query | 5 separate queries (one per entity kind), then sort-merge in memory | O(5 × batch) queries per pull round-trip; at high row counts, each query still does a range scan on the `Version` index — efficient |
| Client upsert | `FindAsync` + `SetValues` per entity, inside one transaction | N individual PK lookups per batch. At 500 rows, this is ~500 SQLite selects + 500 updates in one transaction. Acceptable but not ideal |
| Initial sync | A fresh client pulls the entire dataset from version 0 | With 100 000+ rows, this is 200+ pull round-trips at batch size 500. On a slow connection, this could take minutes |

**Recommendations for initial sync:**
- Offer a "seed bundle" — a pre-built SQLite database snapshot that the client downloads as
  a file and places into OPFS, skipping the pull loop entirely.
- Increase the batch size for initial sync (e.g., 2 000–5 000).
- Show a progress indicator based on `ServerVersionHighWaterMark` vs. the current cursor.

### Push performance

| Factor | Current behavior | Concern at scale |
|--------|-----------------|------------------|
| Batch size | 50 outbox entries per push | Adequate for normal use |
| Outbox query | `WHERE Status IN (Pending, Sending) ORDER BY Id TAKE 50` | Indexed on `Status`; fast |
| Server processing | Sequential: one `FindAsync` + `SaveChangesAsync` per mutation | Each mutation is its own DB round-trip. At 50 mutations, that's 50+ server DB operations per push. Batching into fewer `SaveChangesAsync` calls would help |

### Sync loop timing

The sync loop runs every **60 seconds**. This means:
- Worst-case data staleness: 60 seconds (plus network latency)
- If the user is actively editing offline and goes back online, there's up to a 60-second
  delay before changes reach the server
- The loop does not wake on mutation — the outbox just accumulates until the next cycle

**Mitigation:** Trigger an immediate sync when the browser fires the `online` event, or
when the repository writes to the outbox (debounced).

---

## 4. SQLite-WASM specific constraints

### Single-threaded execution

Blazor WASM runs on the browser's main thread (unless using threading, which this project
does not). Every SQLite operation blocks the UI. The sync service runs via `Task.Run`, but
in single-threaded WASM this is cooperative, not preemptive — a long-running query will
still block rendering.

**Symptoms at scale:**
- UI jank during pull (500 upserts in a tight loop)
- Perceptible delay when opening a view that loads many rows

**Mitigations:**
- Break large pull batches into smaller chunks with `await Task.Yield()` between them to
  let the render loop breathe.
- Use `.NET 8 WASM threading` (experimental) to move SQLite to a web worker.
- Keep queries small and paginated.

### No WAL mode

The project uses SQLite's default journal mode (DELETE). WAL mode would allow concurrent
reads and writes but adds complexity in WASM (the `-wal` and `-shm` files need to persist
alongside the main DB in OPFS). For this workload — single writer (sync service), single
reader (UI) — DELETE mode is adequate.

### Connection string

```
Data Source=fieldservice.db
```

No pragmas are set. Consider adding:

| Pragma | Value | Why |
|--------|-------|-----|
| `journal_mode=wal` | If concurrency becomes an issue | Allows reads during writes |
| `synchronous=normal` | Default is `full` (extra fsync) | Faster writes; safe with OPFS since the browser manages durability |
| `cache_size=-2000` | 2 MB page cache (negative = KB) | Reduces I/O for repeated reads |
| `temp_store=memory` | Temp tables in memory | Avoids temp files in OPFS |

These can be set via `optionsBuilder.UseSqlite("Data Source=fieldservice.db;Cache=Shared")`
or via raw SQL on context creation.

---

## 5. Service worker cache

The service worker precaches the app shell: Blazor runtime (`dotnet.wasm`, framework DLLs),
the app's own assemblies, and static assets (CSS, fonts, icons).

| What | Typical size |
|------|-------------|
| `dotnet.wasm` (Blazor runtime) | ~2–4 MB (compressed) |
| Framework DLLs | ~5–10 MB (compressed) |
| App assemblies | ~500 KB–1 MB |
| Static assets | ~200 KB–1 MB |
| **Total cache** | **~8–16 MB** |

This is cached once on first visit, then updated only when a new build is deployed. Old
caches are deleted on service worker activation.

Sync traffic (`/sync/*`) is **never cached** — it always hits the network, and the sync
service handles offline errors by raising `SyncState.Offline`.

---

## 6. Memory pressure (browser tab)

Blazor WASM loads the .NET runtime + all assemblies into the browser tab's memory. Baseline
memory usage is typically 30–60 MB. On top of that:

| Source | Impact |
|--------|--------|
| EF Core change tracker | Each tracked entity consumes memory. `AsNoTracking()` is used on read queries (good). Mutations track only the affected rows (good). |
| `Include(w => w.LineItems)` | Materializes all line items into memory. At 4 items × 2 000 work orders = 8 000 objects, this adds ~10–20 MB. |
| JSON serialization during sync | Each pull batch deserializes up to 500 JSON payloads. The strings and intermediate objects are GC'd after each batch. |
| Outbox PayloadJson | Each outbox entry stores a full JSON snapshot. If many mutations queue up offline, the outbox scan loads all 50 into memory with their payloads. |

The main risk is the unbounded `ListForTechnicianAsync` query. Everything else is bounded
by batch sizes.

---

## 7. Summary of recommendations

| Priority | Issue | Recommendation |
|----------|-------|----------------|
| **High** | No outbox cleanup | Delete Confirmed entries after N days; prune Failed after user review |
| **High** | Unbounded work order list | Add date/status filter; paginate with `.Take(N)` |
| **High** | No `navigator.storage.persist()` | Call on first load; warn user if denied (especially Safari/iOS) |
| **Medium** | Missing client-side indexes | Add indexes on `WorkOrders.Status`, `Parts.SKU`, composite `(AssignedTechnicianId, ScheduledFor)` |
| **Medium** | Initial sync is slow at scale | Offer a seed bundle or increase batch size for first sync |
| **Medium** | Sync loop is timer-only | Trigger immediate sync on `online` event or outbox write |
| **Medium** | Soft-delete tombstone accumulation | Hard-delete locally after tombstones are confirmed synced |
| **Low** | No SQLite pragmas tuned | Set `synchronous=normal`, `cache_size`, `temp_store=memory` |
| **Low** | Pull upsert is row-by-row | Batch upserts with raw SQL `INSERT OR REPLACE` for faster initial sync |
| **Low** | No full-text search | Add FTS5 virtual table via client migration if search is needed |
