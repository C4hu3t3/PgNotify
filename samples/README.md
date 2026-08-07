# Samples

## [`CacheInvalidation.WebApi/`](CacheInvalidation.WebApi/) — fully implemented

An ASP.NET Core minimal API demonstrating the complete path: `[NotifyChanges]` → a real
`dotnet ef migrations add`-produced migration → the runtime listener →
`IDatabaseNotificationHandler<Product>` → `IMemoryCache` eviction. See its own README for how to run it.

## [`HttpCaching.WebApi/`](HttpCaching.WebApi/) — fully implemented

The same cache-invalidation problem as above, solved without writing a single handler:
`options.AddChangeTracking()` turns every notification into an `IChangeToken`, consumed as an
`IMemoryCache` expiration token and as an HTTP `ETag`/`304 Not Modified` validator. It also differs
from the other samples in two ways worth seeing: it configures notifications with the **fluent API**
(asking for the extended payload explicitly) so it gets the
extended payload's `timestamp`, and it needs no `dotnet ef` at all — the schema and the triggers are
created at startup via `EnsureCreatedAsync()` + `EnsureNotificationTriggersAsync()`. It ships a
`docker-compose.yml` (PostgreSQL on **5433**, so it can run next to `CacheInvalidation.WebApi`), but
runs just as well against any PostgreSQL you point `ConnectionStrings__SampleDatabase` at.

## `TaskBoard.Model/` + `TaskBoard.WebApi/` + `TaskBoard.Watcher/` — fully implemented

Two independent processes sharing one database, implementing the "multiple applications sharing
one database" pattern described below for real instead of just describing it:

- [`TaskBoard.Model/`](TaskBoard.Model/) is the entire shared surface: one `[NotifyChanges]`
  entity, no EF Core, no ASP.NET Core — see its README for why keeping the shared project this
  small matters.
- [`TaskBoard.WebApi/`](TaskBoard.WebApi/) references it to map the entity with EF Core, owns the
  migration, and serves a small web UI (`wwwroot/index.html`) for adding/editing/deleting tasks.
- [`TaskBoard.Watcher/`](TaskBoard.Watcher/) is a console app with **zero EF Core dependency and
  no project reference to `TaskBoard.WebApi`** — it references `TaskBoard.Model` purely for the
  shared `TaskItem` POCO and its `[NotifyChanges]`/`[Table]` configuration, and prints every
  notification it receives via `Events<TaskItem>()`. Run it alongside `TaskBoard.WebApi` and watch
  changes made through the web page show up in its console output in real time.

This is the sample to look at for how little two processes actually need to share to both
participate in the same notification stream — see its READMEs for exactly what's shared (one small
model project) and what isn't (EF Core, Npgsql, or any reference between the two apps themselves).

## [`Orders.Model/`](Orders.Model/) + [`Orders.WebApi/`](Orders.WebApi/) + [`Orders.Projector/`](Orders.Projector/) — fully implemented

A two-process CQRS-style setup exercising the `PgNotify.Writer` and `PgNotify.Listener`
meta-packages end to end, each with a single package reference instead of the individual library
projects every other sample above references directly:

- [`Orders.Model/`](Orders.Model/) is the shared `Order` entity, same minimal-dependency shape as
  `TaskBoard.Model`.
- [`Orders.WebApi/`](Orders.WebApi/) references `PgNotify.Writer` (bundles `EFCore` + `Migrations`
  + `Analyzers`), owns the migration, and serves `/api/orders`.
- [`Orders.Projector/`](Orders.Projector/) references `PgNotify.Listener` (bundles `Runtime` +
  `Runtime.EFCore`) and — unlike `TaskBoard.Watcher` — has its **own** `DbContext`, so it derives
  its channel mapping from its own model (`AddNotificationMappingFromDbContexts()`) instead of
  naming a channel manually. It maintains a denormalized per-customer read-model in memory, served
  over HTTP at `GET /summary`.

Building this sample surfaced a real gap: `PgNotify.Listener` alone wasn't enough to make
`AddNotificationMappingFromDbContexts()` discover a context, because the marker it looks for
(`IsNotificationContext()`) could previously only be added by `UseNpgsqlNotifications()` — which
lives in `PgNotify.Migrations` and additionally replaces the migrations SQL generator, dragging the
whole Npgsql EF Core provider into a process that only ever reads. `Orders.Projector` uses the fix:
`UseNpgsqlNotificationsListening()`, a lighter marker-only method now in `PgNotify.EFCore` (reached
transitively through `PgNotify.Listener`) that `UseNpgsqlNotifications()` itself now calls
internally, so the two stay in sync by construction rather than by convention.

## The rest: design sketches

Each of the following demonstrates a different consumption pattern the library is designed to
support well. They are described at the design level — the wiring pattern and why it fits the
scenario — rather than fully implemented, to keep this pass focused on depth over breadth. Any of
these is a reasonable next sample to build fully; the `CacheInvalidation.WebApi` sample is the
template to start from (`UseNpgsqlNotifications()` + `AddPostgresNotifications(...)`).

### Blazor Server live updates

Blazor Server already holds a persistent connection per circuit, which maps naturally onto
`IPostgresNotificationService.Events<T>()`: a component's `OnInitializedAsync` starts a background
`await foreach` loop over `Events<OrderUpdated>()`, calling `StateHasChanged()` (via
`InvokeAsync(StateHasChanged)`, since the loop runs outside the component's synchronization
context) whenever an update affecting the currently displayed data arrives, and disposes the
subscription (cancels the loop's `CancellationTokenSource`) in `IDisposable.Dispose`. Because
`Events<TEntity>()` is a hot broadcast (one dedicated channel per subscriber via
`EventBroadcaster<T>`), many simultaneously-open circuits each get their own independent stream
without needing per-circuit registration on the runtime side.

### Search index synchronization

A background worker (`BackgroundService`) implementing `IDatabaseNotificationHandler<Product>`
indirectly — really, registering the handler and having it enqueue a re-index job — keeps a search
index (Elasticsearch/Meilisearch/etc.) in sync with the source-of-truth table. The key design
point: the handler should be *idempotent and cheap* (e.g. push the entity's ID onto a debounced
queue rather than calling the search API synchronously inline), since a burst of rapid updates to
the same row will otherwise fire the handler once per row-version. `OnUpdate(x => new { ... })`
watching only the columns actually reflected in the search index (not every column) meaningfully
reduces this churn at the source.

### Multiple applications sharing one database

See [`TaskBoard.WebApi/`](TaskBoard.WebApi/) + [`TaskBoard.Watcher/`](TaskBoard.Watcher/) above for
a fully implemented version of this scenario. Because channel naming is entirely deterministic from
entity configuration (see
`docs/architecture.md`), independent applications/services that all reference the same entity
model (or independently declare equivalent `[NotifyChanges]` entities pointing at the same tables)
agree on channel names without coordination — service A can `INSERT`/`UPDATE` and service B, C,
... can each independently `AddPostgresNotifications()` against the same connection string and
receive the same notifications, with no shared code beyond the table schema itself. The main
caveat: PostgreSQL's `NOTIFY` payload has an 8000-byte limit shared across *all* listeners on that
channel, so a large `changed`/extended payload is a shared cost — prefer the minimal payload plus
a follow-up fetch for scenarios with many independent listeners and large rows.
