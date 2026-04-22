# FieldService — Offline-First .NET PWA Sketch

A working architectural sketch of a Blazor WebAssembly PWA that keeps a non-trivial relational
domain (customers, work orders with line items, parts inventory, technicians) usable offline and
synchronizes it to an ASP.NET Core backend when network is available.

## Stack

| Layer              | Technology                                                 |
|--------------------|------------------------------------------------------------|
| Client shell       | Blazor WebAssembly PWA (`dotnet new blazorwasm --pwa`)     |
| Client local store | SQLite-WASM via `Microsoft.EntityFrameworkCore.Sqlite`     |
| Client cache       | Service worker (precache) + Background Sync API            |
| Transport          | `HttpClient` + SignalR (live push when online, optional)   |
| Server API         | ASP.NET Core Minimal APIs                                  |
| Server store       | SQL Server or PostgreSQL via EF Core                        |
| Auth               | ASP.NET Core Identity + JWT (refresh tokens cached locally)|
| Sync model         | Hand-rolled outbox + version-cursor pull (see below)       |

We deliberately hand-roll sync instead of adopting DotMim.Sync so the protocol is legible and
conflict semantics are explicit. The protocol is ~60 LoC of DTOs; the client worker is ~200 LoC.

## Project layout

```
FieldService/
├── FieldService.sln
├── src/
│   ├── FieldService.Shared/          # Domain types + sync protocol DTOs (target netstandard2.1)
│   ├── FieldService.Server/          # ASP.NET Core API + EF Core (target net8.0)
│   └── FieldService.Client/          # Blazor WASM PWA (target net8.0)
```

## Sync model in one picture

```
  ┌───────────── Client (Blazor WASM) ─────────────┐          ┌──── Server (ASP.NET Core) ────┐
  │                                                │          │                               │
  │   UI ──► Repository ──► LocalDbContext ──┐     │          │   AppDbContext ──► SQL DB     │
  │                             │            │     │          │         ▲                     │
  │                             ▼            │     │ POST     │         │                     │
  │                       outbox table  ─────┼─────┼─────────►│  /sync/push  (idempotent)     │
  │                                          │     │          │         │                     │
  │                       cursor row  ◄──────┼─────┼──────────│  /sync/pull?since=N           │
  │                             ▲            │     │  GET     │                               │
  │                             └────────────┘     │          │                               │
  └────────────────────────────────────────────────┘          └───────────────────────────────┘
```

Every entity carries two metadata fields: a monotonic `Version` (server-assigned, global sequence)
and an `IsDeleted` tombstone. The client asks `GET /sync/pull?since={lastVersion}` to receive all
changes newer than its cursor and advances the cursor. Mutations the user makes offline are written
to the local store immediately **and** appended to an `Outbox` table; a background worker flushes
the outbox to `POST /sync/push`, which returns per-mutation outcomes (`Applied`, `Conflict`,
`Rejected`). Conflicts return the current server payload so the client can re-apply on top or prompt
the user.

## How schema upgrades work

Two independent migration ladders, coordinated by a **protocol version**:

1. **Server schema.** Standard EF Core migrations (`dotnet ef migrations add …`). Backwards-
   compatible shape changes (nullable columns with defaults) ship freely. Breaking changes bump the
   protocol version and the server advertises supported versions on `/sync/capabilities`.
2. **Client schema.** A tiny hand-rolled migration runner stores a `schema_version` row in SQLite
   and executes `ILocalMigration` implementations in order on app start. This runs inside the
   browser against the WASM-hosted SQLite, before any repository touches the DB.
3. **Protocol version.** Every sync request sends `X-FieldService-Protocol: 2`. If the server sees
   a version it can't serve, it replies `426 Upgrade Required` and the client shows an "update
   available" banner instead of corrupting data.

A worked example of each appears in `docs/UPGRADES.md` and in the `Migrations/` folders.

## File index

Start reading in this order:

1. `src/FieldService.Shared/Domain/` — the domain objects with their sync metadata
2. `src/FieldService.Shared/Sync/` — the wire protocol DTOs
3. `src/FieldService.Server/Sync/SyncEndpoints.cs` — server push/pull handlers
4. `src/FieldService.Client/Data/LocalDbContext.cs` — client EF Core over SQLite-WASM
5. `src/FieldService.Client/Sync/SyncService.cs` — the background sync worker
6. `src/FieldService.Client/Data/Migrations/` — client schema migration ladder
7. `docs/UPGRADES.md` — worked example of a breaking schema change

## Docker build and deploy

The solution includes containerization for both the client (Blazor WASM served by NGINX) and server API.

```bash
docker compose build
docker compose up -d
```

- Client URL: `http://localhost:8081`
- API URL: `http://localhost:8081/sync/*` (proxied to the server container)
- SQLite file is persisted in the named volume `fieldservice_data`.

To stop and remove containers:

```bash
docker compose down
```
