# Performance notes

## Database side (trigger overhead)

- Every `AFTER` trigger adds work to the transaction that fired it — a `pg_notify()` call is
  cheap (it just queues an entry in shared memory for `LISTEN`ing backends to pick up), but it is
  not free, and it runs synchronously within the triggering statement.
- **`OnUpdate` without a property selector watches every mapped column.** This means an `UPDATE`
  touching any column runs the full `IS DISTINCT FROM` comparison across every column to decide
  whether to notify — for wide tables with frequent partial updates, an explicit
  `OnUpdate(x => new { x.Name, x.Status })` both narrows *when* a notification fires (the whole
  point) and reduces the guard's own per-row cost.
- The `changed` array (extended payload) for a filtered `OnUpdate` only compares the watched
  columns, not every mapped column — another reason to prefer the minimal watched set over "watch
  everything."
- One trigger function serves `INSERT`/`UPDATE`/`DELETE` uniformly (branching on `TG_OP`), which
  keeps `pg_proc` small and avoids the (larger) alternative of three separate trigger functions
  per entity — but it does mean the function is invoked (and does the `CASE`-based branch check)
  for operations that end up doing nothing, if you ever inspect `EXPLAIN ANALYZE` on a write and
  see the trigger function called for an operation it's not configured for. In practice this
  shouldn't happen: `CREATE TRIGGER ... AFTER {only the configured operations} ...` means the
  trigger — and therefore the function — is never invoked for an operation that wasn't configured
  at all.

## Runtime side (.NET)

- **Routing is a dictionary lookup, and there is no reflection on the hot path at all.**
  `NotificationChannelMap` resolves `(channel, entity name)` to an `IEntityNotificationDispatcher`
  compiled once, when the channel is mapped. Handlers and `Events<TEntity>()` streams both receive
  the `NotificationEnvelope`, so nothing is deserialized into a typed shape unless a consumer asks
  for it with an explicit `envelope.ToTyped<T>()` — the only place `System.Text.Json` runs per
  notification is the envelope parsing itself.
- **Only one handler group is resolved per notification.** The operation-specific interfaces are
  separate types rather than three default-implemented methods on one, so an `UPDATE` resolves
  `IDatabaseUpdatedHandler<T>` and never touches the insert/delete handlers — and an entity whose
  operation nobody handles resolves nothing.
- **One scoped `IServiceProvider` per notification**, not per handler — all handlers for one
  notification share the same scope (and therefore
  the same scoped services, e.g. one `DbContext` instance if a handler needs one).
- **Measured cost of one notification through the whole pipeline** (arm64, .NET 10.0.10, no
  middleware, one handler registered): **388 ns, 32 B allocated**. Routing is 51 ns of that; the
  rest is resolving and invoking the handler group. With no handler registered at all, dispatch
  allocates nothing.
- **`Events<TEntity>()` fan-out is allocation-conscious but not zero-allocation**: each subscriber gets
  its own `System.Threading.Channels.Channel<T>` (unbounded, single-reader), and publishing
  writes to every live subscriber's channel. A notification type with many concurrent `Events<TEntity>()`
  subscribers pays a per-subscriber write; this is the right tradeoff for a "every subscriber sees
  every event independently" (hot observable) semantics, but if you have a very large number of
  concurrent subscribers for the same event type, prefer one shared background consumer that
  fans out via your own mechanism (e.g. a `Channel<T>` you manage) over many independent
  `Events<TEntity>()` calls.
- **The middleware pipeline is built once**, at DI registration time, by composing
  `IEnumerable<INotificationMiddleware>` into a single delegate chain — not rebuilt per
  notification (mirrors how ASP.NET Core composes its middleware pipeline once).
- **The default JSON deserializer parses each payload once** into a `JsonDocument`/`JsonElement`
  tree for envelope fields (`entity`, `operation`, `keys`, `changed`, `timestamp`), and
  `ToTyped<T>()` does a second, independent `JsonSerializer.Deserialize<T>` pass over the raw
  payload string for the typed conversion. For very high-throughput scenarios where this matters,
  a custom `INotificationPayloadDeserializer` could parse once and cache the `JsonDocument`, but
  in practice `NOTIFY` payloads are capped at 8000 bytes, so this cost is bounded and small.

## Notification bursts, and why only one consumer coalesces them

Triggers are `FOR EACH ROW`, so a single statement produces one `NOTIFY` per affected row: an
`UPDATE` touching 200 rows delivers 200 notifications, not one. There is no statement-level
batching to be had — a `FOR EACH STATEMENT` trigger has no `NEW`/`OLD` row to build a payload
from, which is the whole point of the payload.

The two consumers of that burst are deliberately treated differently:

- **Change tracking coalesces.** `AddChangeTracking(window)` collapses a burst into one *leading*
  invalidation (immediate, so no reader is served stale data during the window) plus one
  *trailing* invalidation per window that still has pending changes. In
  [`samples/HttpCaching.WebApi`](../samples/HttpCaching.WebApi) with a 200 ms window, a 201-row
  `UPDATE` produces 201 received notifications and exactly one cache miss. The window defaults to
  `TimeSpan.Zero` — i.e. off, one invalidation per notification — so nobody pays unrequested
  latency by accident.
- **Handler dispatch does not coalesce.** Every registered handler for the entity and every
  `Events<TEntity>()` subscriber sees all 201 notifications.

That asymmetry is a semantic one, not an oversight. A change token is convergent: dropping a
redundant invalidation is unobservable, because the *only* information it carries is "re-read".
A handler is not — it may be projecting into a read model, appending to an audit trail, or
reading the extended payload's `changed` array, where a collapsed notification silently loses the
intermediate values, and where a coalesced `Update`/`Delete` pair can converge to the wrong result.
Coalescing that path would trade an at-least-once guarantee for a lossy one, invisibly.

So the library ships no debounce for handler dispatch, and a general-purpose debounce middleware
would be the wrong shape for it in any case: `INotificationMiddleware` runs inline in the listener's
dispatch loop (delaying there stalls every channel behind it), `NotificationContext.Services` is
disposed as soon as the pipeline returns (so a detached, deferred continuation has no usable DI
scope), and short-circuiting into a queue puts the work outside the reach of
`RetryNotificationMiddleware`. Two levers to use instead, in order of effectiveness:

1. **Cut the burst at the source with `OnUpdate(x => new { ... })`.** The `IS DISTINCT FROM` guard
   runs inside the trigger, so an `UPDATE` that doesn't change a watched column produces no
   `NOTIFY` at all — nothing is sent, queued, parsed, or dispatched. Nothing on the .NET side can
   beat not emitting the notification.
2. **Keep handlers cheap and coalesce in the application.** For expensive work (search indexing,
   external API calls), have the handler enqueue the envelope's `keys` onto a queue keyed by
   primary key and let a separate worker apply the latest state per key. This keeps the coalescing
   decision — and its correctness assumptions — in the code that knows whether they hold.

## Metrics

`options.UseMetrics()` records `notifications.received`, `notifications.failed`, and
`notifications.dispatch.duration` (histogram, milliseconds) via `System.Diagnostics.Metrics`
under the meter name `PgNotify` — wire up any
`System.Diagnostics.Metrics`-compatible exporter (OpenTelemetry's
`AddMeter("PgNotify")` is the common case) to watch
dispatch latency and failure rate in production without adding a hard dependency on any specific
observability stack.

## Caching

`NotificationEntityConfiguration` is computed from annotations on demand
(`entityType.GetNotificationConfiguration()`) rather than cached by this library explicitly —
this is intentional, not an oversight: EF Core's compiled model is itself cached (per
`DbContext` type, by default), so repeated calls against the same compiled model resolve
annotations from an already-in-memory dictionary. Adding a second cache on top would just be
caching a cache.
