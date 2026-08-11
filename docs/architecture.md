# Architecture

## Goals and non-goals

The library makes PostgreSQL `LISTEN`/`NOTIFY` feel like a first-class part of EF Core: you
configure notifications the same way you configure everything else in EF Core (attributes or
fluent API on `ModelBuilder`), migrations generate the trigger DDL automatically, and a runtime
listener turns raw `NOTIFY` payloads into strongly-typed .NET events.

It deliberately does **not** try to be a general-purpose outbox/event-sourcing framework, a
message bus, or a replacement for logical replication / CDC (Debezium etc.) for high-volume
streaming use cases. `LISTEN`/`NOTIFY` has real limits — see [Limits](#limits-and-when-not-to-use-this)
below — and the library is built around them rather than around hiding them.

## Solution layout

```
src/
  PgNotify.Core            # provider-agnostic vocabulary: payload model, channel-naming
                                 # strategies, event/envelope contracts. No EF, no Npgsql.
  PgNotify.EFCore          # [NotifyChanges], HasDatabaseNotifications(), conventions,
                                 # the annotation-backed configuration model. Depends only on
                                 # Microsoft.EntityFrameworkCore.Relational.
  PgNotify.Migrations      # UseNpgsqlNotifications(): the only project that references
                                 # Npgsql.EntityFrameworkCore.PostgreSQL. Turns annotations into
                                 # trigger/function DDL via a custom IMigrationsSqlGenerator.
  PgNotify.Runtime         # The LISTEN connection, reconnect, dispatch pipeline, handlers,
                                 # envelope streams, DI, health check. No EF Core dependency.
  PgNotify.Runtime.EFCore  # Derives the listener's channels and connection string from the
                                 # models of the DbContexts that opted in.
  PgNotify.Analyzers       # PGN001–PGN004 compile-time diagnostics.
  PgNotify.Writer          # Empty meta-package: EFCore + Migrations + Analyzers.
  PgNotify.Listener        # Empty meta-package: Runtime + Runtime.EFCore.
tests/                          # One test project per src/ project, plus PgNotify.IntegrationTests
samples/                        # CacheInvalidation.WebApi (full) + design sketches for others
```

**Why this split, specifically:**

- `Core` has zero EF/Npgsql coupling on purpose: it's the shared vocabulary between the
  design-time side (EFCore/Migrations, which describe *what SQL to generate*) and the runtime
  side (Runtime, which describes *what to do with a received notification*). Neither side needs to
  know about the other's package.
- `EFCore` and `Migrations` are separate projects specifically so the annotation *model*
  (provider-neutral: "this entity watches these columns, uses this channel strategy") is
  decoupled from the Npgsql-specific *SQL generation* that consumes it. Today only Npgsql
  consumes it, but nothing about the annotation model is Npgsql-specific.
- `Runtime` has no EF Core dependency so a pure listener process (no `DbContext`, just
  `AddPostgresNotifications()`) is possible — useful for a dedicated notification-fanout
  service that doesn't otherwise touch the database.
- `Runtime.EFCore` is where the two sides meet, and it is a separate package precisely so that
  property survives: it turns a `DbContext`'s model into the listener's channel map through
  `Runtime`'s public `INotificationMappingSource`, referencing EF Core *Relational* and never the
  Npgsql provider — reading a model should not require carrying the migrations stack. A process
  that has no `DbContext` simply doesn't reference it and declares its channels with
  `MapChannel(...)`.
- `Writer` and `Listener` are empty meta-packages (no code, just `PackageReference`s) so a
  consumer can install one package per process role instead of picking individual ones by hand. A
  project that only *declares* entities needs neither: `[NotifyChanges]` lives in `Core`, so
  `samples/TaskBoard.Model` — the project `TaskBoard.WebApi` (a `Writer` consumer) and
  `TaskBoard.Watcher` (a `Runtime`-only listener) share — references `Core` alone. A single-process
  app like `CacheInvalidation.WebApi` just references both meta-packages.

## Lifecycle

```mermaid
sequenceDiagram
    participant App as Application (EF Core SaveChanges)
    participant PG as PostgreSQL
    participant Listener as NpgsqlNotificationListener
    participant Pipeline as Dispatch pipeline
    participant Handler as IDatabase…Handler<TEntity> / Events<T>()

    App->>PG: INSERT/UPDATE/DELETE
    PG->>PG: AFTER trigger fires fn_{table}_notify()
    Note over PG: json_build_object(...) built entirely in SQL;<br/>IS DISTINCT FROM guards watched-column updates
    PG-->>Listener: NOTIFY channel, payload (async, over the LISTEN connection)
    Listener->>Pipeline: NotificationContext { Envelope, scoped IServiceProvider }
    Pipeline->>Pipeline: UseLogging() / UseRetry() / UseMetrics() / custom middleware
    Pipeline->>Handler: publish to Events<T>() (deserialized), then invoke the entity's handlers (envelope)
```

Two things worth calling out:

1. **The payload is built entirely in SQL, at `NOTIFY` time — not fetched afterward.** An
   alternative design would send a lightweight "something changed" ping and have .NET code fetch
   the row. That was rejected: it reintroduces a race window between the trigger firing and the
   fetch (the row may have changed again, or been deleted, by the time you read it), it can't
   report a pre-image or "what changed," and it doubles the round trips for every notification.
   The cost is that the payload shape must be decided at migration-generation time
   (`INotificationPayloadBuilder`), not per-notification at runtime — and that payload size becomes
   the trigger's problem, since `pg_notify` raises `22023` above 7999 bytes and that abort takes
   the writing transaction with it. The generated function therefore measures the payload with
   `octet_length` and substitutes a reduced one carrying `"truncated": true` rather than letting it
   raise (`NotificationPayloadOverflow`). A size check rather than a PL/pgSQL `EXCEPTION` block:
   `BEGIN … EXCEPTION` opens a subtransaction on every invocation, so every write would pay for a
   case that almost never happens. The check is skipped for the minimal payload, which is already
   what the fallback would send.
2. **Dispatch happens on a background loop reading one dedicated connection**, not on the
   connection that did the `INSERT`/`UPDATE`/`DELETE`. `LISTEN` registrations are
   connection-scoped, so the listener owns a connection that is never returned to any pool and
   is never used for anything except `LISTEN` + waiting.

## The migrations pipeline in detail

This is the subsystem most tightly coupled to EF Core internals, so it's worth being precise
about how it fits together (see `docs/migrations.md` for the generated SQL shapes themselves).

1. **Configuration → annotations.** `HasDatabaseNotifications()` and `[NotifyChanges]` both
   converge on the same internal step (`NotificationConfigurationWriter`), which writes a fixed
   set of *primitive* (`bool`/`string`) annotations onto the `IMutableEntityType` — deliberately
   not live objects, so they survive being written into `ModelSnapshot.cs` as plain C# literals
   and read back by the design-time tooling.
2. **Annotations → migrations diff signal.** `NpgsqlNotificationsAnnotationProvider` (subclassing
   Npgsql's own `IRelationalAnnotationProvider`) computes a single deterministic *fingerprint*
   string and attaches it to the table, design-time only. The fingerprint is a hash of the trigger
   SQL this library would generate for the table — not a summary of the configuration — so it also
   moves when the *library* changes what it generates, and when the table's real column set
   changes under an unfiltered `OnUpdate()`. A readable prefix (operations, watched columns,
   channel names) is kept in front of the hash purely so `ModelSnapshot.cs` diffs stay
   interpretable. EF Core's
   `MigrationsModelDiffer` already knows how to diff arbitrary annotations between the "old"
   (reconstructed from `ModelSnapshot.cs`) and "new" (freshly built) models — when the fingerprint
   differs, an `AlterTableOperation` carrying both old and new fingerprints is emitted; a new
   table gets a `CreateTableOperation` with the new fingerprint; `IMigrationsAnnotationProvider`
   (a separate service EF Core uses specifically for *removal* operations) supplies the same
   fingerprint onto `DropTableOperation`.
3. **Fingerprint → SQL.** `NpgsqlNotificationsMigrationsSqlGenerator` overrides the
   `CreateTableOperation`/`AlterTableOperation`/`DropTableOperation` handlers. It never tries to
   decode the fingerprint string — that's purely a change-detection signal. Instead, when it sees
   the fingerprint present (or changed), it re-resolves the full, rich
   `NotificationEntityConfiguration` straight from the model (`model.GetRelationalModel().FindTable(...)`)
   and hands it to `NotificationTriggerSqlBuilder`, which renders the actual
   `CREATE OR REPLACE FUNCTION` / `DROP TRIGGER IF EXISTS` / `CREATE TRIGGER` statements.

One consequence of step 1 worth knowing: because entity-type-level annotations (not the
migrations fingerprint) are what round-trips through `ModelSnapshot.cs`, and EF Core reconstructs
that snapshot's entity types via the *string-named* `ModelBuilder.Entity(string)` overload, the
reconstructed "old" side of a diff has `IEntityType.ClrType == typeof(Dictionary<string, object>)`
— **not** the real POCO type. Anything computed from an entity's identity for fingerprinting or
payload purposes must use `IReadOnlyEntityType.ShortName()` (backed by the stable metadata
`Name`), never `ClrType.Name`. This was a real bug caught by running `dotnet ef migrations add`
against the sample project — the programmatic differ tests in `PgNotify.Migrations.Tests`
build both sides of a diff freshly and can't see it, which is why the sample's `dotnet-ef` round
trip is part of this project's verification, not just the unit tests.

## Runtime dispatch

- **Connection**: one dedicated `NpgsqlConnection`, opened by `NpgsqlNotificationListener`,
  never pooled. On (re)connect it issues `LISTEN` for every channel the type registry knows
  about, then loops on `NpgsqlConnection.WaitAsync(CancellationToken)`.
- **Reconnect**: `IReconnectPolicy` (default `ExponentialBackoffReconnectPolicy`, with jitter)
  decides the delay after a failure; the listener's run loop retries until cancelled or the
  policy gives up.
- **Routing**: incoming notifications carry `(channel, entity, operation)`, resolved from the
  JSON payload by `INotificationPayloadDeserializer`. Handlers are routed by
  `NotificationChannelMap`, a `(channel, entity name) -> entity type` dictionary declared by
  `MapChannel<TEntity>(...)` or derived from a model; the operation is not part of the key because
  the payload already states it, and it selects the handler *group* rather than the entry. Handlers
  and `Events<TEntity>()` streams share that one lookup — both are keyed on the entity type.
- **Fan-out**: the terminal step (1) publishes the envelope to `NotificationEventHub`, backing every
  live `Events<TEntity>()` subscriber (each with its own `Channel<T>`), then (2)
  invokes the entity's handlers from a **scoped** `IServiceProvider` created per notification:
  the group matching the operation (`IDatabaseInsertedHandler<T>`/`Updated`/`Deleted`), then
  `IDatabaseNotificationHandler<T>`, then the non-generic `IDatabaseNotificationHandler`. Handlers
  receive the `NotificationEnvelope` itself and pay no deserialization; a projected payload is
  bound explicitly with `envelope.ToTyped<T>()`.
- **Middleware**: `INotificationMiddleware` wraps the terminal dispatcher the same way ASP.NET
  Core middleware wraps a request pipeline. `UseLogging()`/`UseRetry()`/`UseMetrics()` are built
  in; `UseMiddleware<T>()` registers a custom one, resolved from DI, in call order.

## Change tracking (`IChangeToken`)

`options.AddChangeTracking()` exposes each entity's last change as an `IChangeToken`, for
`IMemoryCache` expiration (`entry.AddExpirationToken(...)`), `ChangeToken.OnChange`, or HTTP
`ETag`/`Last-Modified`:

```csharp
public sealed class OrdersController(IEntityChangeTracker<Order> tracker, IMemoryCache cache)
{
    public IActionResult Get()
    {
        var etag = $"\"{tracker.LastModified.UtcTicks}\"";
        // ... 304 handling ...
        return cache.GetOrCreate("orders", entry =>
        {
            entry.AddExpirationToken(tracker.GetChangeToken());
            return Query();
        });
    }
}
```

Three deliberate design points, all in `src/PgNotify.Runtime/Caching/`:

- **It's a middleware, not an `IExternalNotificationPublisher`.** Trackers are keyed on the
  envelope's entity name, so one tracker covers every operation on an entity, and entities sharing
  a channel under `SingleChannelNamingStrategy` are tracked even with no registered event type of
  their own. The envelope also carries the trigger's `timestamp`, which typed event records don't.
- **`LastModified` comes from the trigger's clock, not from receive time.** Every instance behind
  a load balancer therefore derives the *same* `ETag` for the same write — receive time would give
  each instance its own value and break conditional requests as soon as a client is routed
  elsewhere. It is advanced monotonically, since concurrent transactions can deliver timestamps
  slightly out of order. Payloads without a `timestamp` field (the minimal payload builder) fall
  back to receive time.
- **The swapped-out `CancellationTokenSource` is never disposed.** `CancellationTokenSource.Token`
  throws `ObjectDisposedException` after disposal, and a concurrent `GetChangeToken()` can be
  holding the old reference — as can `CancellationChangeToken`, which registers its callbacks on
  that same source. `EntityChangeTrackerTests.Reading_tokens_concurrently_with_changes_never_throws`
  is the regression guard.

The optional coalescing window (`AddChangeTracking(TimeSpan.FromMilliseconds(200))`) exists because
a bulk `UPDATE` emits one row-level `NOTIFY` per row: without it, 500 updated rows means 500 token
invalidations and 500 cache stampedes. Invalidation stays on the leading edge (never delayed); only
the *extra* invalidations within the window are collapsed into one trailing invalidation, so a
change is never silently dropped. Handler dispatch is deliberately *not* coalesced the same way —
see [`performance.md`](performance.md#notification-bursts-and-why-only-one-consumer-coalesces-them)
for why the two consumers differ and what to do about an expensive handler instead.

## Extensibility points

Every one of these is a plain interface with a documented default implementation:

| Concern | Interface | Default |
|---|---|---|
| Channel naming | `INotificationChannelNamingStrategy` | `PerEntityChannelNamingStrategy` |
| Payload shape | `INotificationPayloadBuilder` | `MinimalNotificationPayloadBuilder`, either configuration style |
| Payload parsing | `INotificationPayloadDeserializer` | `DefaultNotificationPayloadDeserializer` |
| Reconnect behavior | `IReconnectPolicy` | `ExponentialBackoffReconnectPolicy` |
| Dispatch pipeline stage | `INotificationMiddleware` | `Logging`/`Retry`/`Metrics` (opt-in) |
| Channel/connection discovery | `INotificationMappingSource` | none (`Runtime.EFCore` provides one) |

Custom channel-naming strategies and payload builders must have a public parameterless
constructor: they're referenced from annotations by `AssemblyQualifiedName` and reconstructed via
`Activator.CreateInstance` when the design-time tooling needs to reconstruct
`NotificationEntityConfiguration` outside the process that originally configured it — this is the
one place the library uses reflection outside of one-time startup registration, and it's
documented and validated eagerly (a missing parameterless constructor fails fast at model-build
time via `NotificationValidationConvention`).

## Limits and when not to use this

- **PostgreSQL `NOTIFY` payloads are capped at 8000 bytes.** The extended payload (with `changed`
  and full `keys`) can exceed this for wide tables or large composite keys; prefer the minimal
  payload or a custom, smaller `INotificationPayloadBuilder` for such entities.
- **`LISTEN`/`NOTIFY` is fire-and-forget and not durable.** If no process is listening when a
  notification fires, it is lost — there is no replay, no queue, no at-least-once guarantee. This
  is a deliberate default, not an oversight (see the non-goals above) — `NotificationDeliveryMode.LogicalReplication`
  (below) is the library's own opt-in answer for entities that need durability instead of pairing
  this with an external outbox pattern.
- **Every listener on a channel receives every notification on it.** There's no per-consumer
  filtering at the database level beyond channel choice — that's what the three channel
  strategies are for (see `docs/migrations.md`).
- **Triggers add write-path overhead.** `AFTER` triggers with an `IS DISTINCT FROM` guard are
  cheap, but they are not free; see `docs/performance.md`.

## `NotificationDeliveryMode.LogicalReplication`: the opt-in durability escape hatch

An entity can opt out of the trigger/`NOTIFY` path above entirely and instead have its changes read
from PostgreSQL's write-ahead log through a logical replication slot
(`WithDelivery(NotificationDeliveryMode.LogicalReplication)` or
`[NotifyChanges(Delivery = NotificationDeliveryMode.LogicalReplication)]`) — see
`docs/plans/logical-replication-delivery.md` for the full design and how it was verified. A slot
retains WAL until the listener confirms a position, so a disconnected listener loses nothing and
resumes exactly where it left off: at-least-once delivery, at the cost of a real, non-default set
of operational prerequisites `Notify` does not have:

- **`wal_level = logical` on the server.** A server-level setting migrations cannot set for you —
  provision it yourself (and expect a restart to be required on an existing cluster).
- **A role with the `REPLICATION` attribute** for the connection
  `PgNotify.Runtime.Replication` streams over — a distinct permission from the normal DML
  connection.
- **No trigger, but no free lunch either**: `WithReplicaIdentityFull()` (needed for a filtered
  `OnUpdate(...)` to be enforceable at all — there is no old row to compare against otherwise)
  makes every write to the table log its full old row to WAL, not only writes this library reads;
  see `docs/performance.md` for measured overhead.
- **An orphaned replication slot pins WAL on the server indefinitely** until something confirms
  past it — worse than an orphaned trigger, which is merely dead code. Nothing in this library
  drops a slot automatically, on purpose (see `docs/plans/logical-replication-delivery.md`'s
  "Accepted costs"); `DatabaseFacade.FindOrphanedNotificationReplicationSlots()` gives visibility,
  not remediation.
- **No `TRUNCATE` equivalent.** A `TRUNCATE` on a `LogicalReplication`-configured table is logged
  and skipped, not translated into a notification — there is no `OnTruncate` on either delivery
  mode today.
- **The extended payload's `changed` field needs `REPLICA IDENTITY FULL` to mean anything.**
  Without it, an update's old values are unavailable to the replication stream, so `changed` comes
  back empty rather than guessed — a real, documented behavioral difference from the same
  configuration under `Notify`, where the trigger's own `IS DISTINCT FROM` guard needs no such
  setting.

Despite the different transport, a `LogicalReplication`-delivered notification reaches handlers and
`Events<TEntity>()` streams identically to a `Notify`-delivered one — same dispatch pipeline, same
envelope shape for the same payload configuration (`NotificationPayloadJsonMaterializer` produces
the exact JSON the trigger would have, then hands it to the same `INotificationPayloadDeserializer`)
— so switching an entity's delivery mode changes none of the consuming code, only what it costs to
run and what it guarantees when nobody was listening.
