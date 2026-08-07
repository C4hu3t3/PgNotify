# Orders.Projector

The listen side of the [`Orders` sample](../README.md#orderswebapi--ordersprojector--fully-implemented):
a background CQRS-style projector that turns `Order` change notifications into a denormalized
per-customer read-model, kept in memory and served over HTTP.

## What it shows

- One package reference to the library, not two: `PgNotify.Listener` bundles `Runtime` +
  `Runtime.EFCore` (and `Runtime.EFCore` itself pulls in `EFCore`, for the marker and
  `AddNotificationMappingFromDbContexts()` this project uses). Compare to
  [`TaskBoard.Watcher`](../TaskBoard.Watcher), which references `PgNotify.Runtime` alone because it
  has no `DbContext` of its own — this project deliberately does, to show the complementary case.
- `ProjectorDbContext.cs` maps the same `Order` table as `Orders.WebApi`'s `SampleDbContext`, from
  a completely separate process, but is never migrated from here: `Orders.WebApi` owns the schema.
  It's used two ways — to derive this process' channel and connection string
  (`AddNotificationMappingFromDbContexts()` in `Program.cs`), and by `OrderProjectionHandler` to
  re-read a row after a notification names it.
- `Program.cs` calls **`UseNpgsqlNotificationsListening()`**, not the full
  `UseNpgsqlNotifications()` — a fix that came out of building this sample. The full method (from
  `PgNotify.Migrations`) also replaces the context's migrations SQL generator, which only makes
  sense for a context that owns the schema; a listen-only context has no use for it, and pulling in
  `PgNotify.Migrations` just for the marker would mean carrying the whole Npgsql EF Core provider
  into a process that only ever reads. `UseNpgsqlNotificationsListening()` (from `PgNotify.EFCore`,
  reached transitively through `PgNotify.Listener`) adds just the marker
  `AddNotificationMappingFromDbContexts()` looks for.
- `OrderProjectionHandler.cs` — one method implicitly implements `IDatabaseInsertedHandler<Order>`,
  `IDatabaseUpdatedHandler<Order>` and `IDatabaseDeletedHandler<Order>` at once (their signatures
  are identical). Insert/update re-read the row through `ProjectorDbContext` — the minimal payload
  never carries `CustomerName`/`Amount`, only the changed row's id — and upsert it into
  `SummaryStore`; delete just removes it, since the row is already gone.

## Running it

Needs [`../Orders.WebApi`](../Orders.WebApi) running first (or at least its migration applied),
against the same database:

```bash
dotnet run --urls http://localhost:5200
```

```bash
curl -s localhost:5200/summary
```

Create/edit/delete a few orders through `Orders.WebApi`'s API and watch the totals here update —
including for rows changed through a raw `psql` session or a third process, since this reacts to
what the database says happened, not to calls made through `Orders.WebApi` itself.
