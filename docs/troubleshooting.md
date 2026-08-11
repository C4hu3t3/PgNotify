# Troubleshooting

## Notifications never arrive

1. **Is `AddPostgresNotifications()` actually registered and running?** Configuring
   `HasDatabaseNotifications()`/`[NotifyChanges]` only sets up the database side (the trigger).
   Nothing consumes it without a running `PgNotify.Runtime` host somewhere. The
   `PgNotify.Analyzers` package's **PGN003** diagnostic catches the most common version of
   this mistake at compile time (configured but no `AddPostgresNotifications(...)` call anywhere
   in the compilation) — but it can't see registrations that happen in a different project/service.
2. **Did the migration actually apply?** Check `pg_trigger`/`pg_proc` directly:
   ```sql
   SELECT tgname FROM pg_trigger WHERE NOT tgisinternal;
   SELECT proname FROM pg_proc WHERE proname LIKE 'fn_%_notify';
   ```
   If they're missing, the migration was generated but not run (`dotnet ef database update` /
   `Database.Migrate()`), or `UseNpgsqlNotifications()` wasn't chained onto `UseNpgsql(...)` for
   the `DbContextOptionsBuilder` used to build/apply that migration.
3. **Check the health check** (`GET /health` if you wired one up, or resolve
   `PostgresNotificationHealthCheck` directly): `Unhealthy` means the dedicated LISTEN connection
   isn't currently connected — see [Listener won't (re)connect](#listener-wont-reconnect) below.
   `Healthy` with a very old `lastNotificationAt` means the connection is fine but nothing has
   fired — go back to step 2.
4. **Channel name mismatch.** See
   [A hand-written channel name never matches](#a-hand-written-channel-name-never-matches)
   below — this is the single most common cause of "the trigger fires (confirmed via `pg_proc`/a
   raw `LISTEN` in `psql`) but my handler never runs."

## A hand-written channel name never matches

The channel is the mapped table name (under the default strategy), and EF Core decides that name —
a `DbSet<Product> Products` property makes it `"Products"`, not `"Product"`. Any channel name typed
by hand can therefore disagree with the trigger's, and a disagreement produces no error at all: the
listener subscribes to a channel nothing notifies.

**Fix**: don't type it. `options.AddNotificationMappingFromDbContexts()` (inside
`AddPostgresNotifications(options => ...)`) reads the channels out of
the same model the triggers are generated from, so the two cannot drift. Where that is impossible —
a listener process with no `DbContext` — pin the table with `[Table("...")]` in the project the two
processes share, so the name is decided in one place rather than by a `DbSet` property the listener
cannot see (`samples/TaskBoard.Model` does exactly this).

## Malformed payload / deserialization errors

`INotificationPayloadDeserializer.Deserialize` throws `NotificationPayloadFormatException` for:
JSON that doesn't parse, a missing `entity`/`operation` field, or an unrecognized `operation`
value. The default listener logs these as warnings and continues (a single malformed notification
never crashes the listener). Common causes:

- **A custom `INotificationPayloadBuilder` whose JSON field names don't match**
  `DefaultNotificationPayloadDeserializer`'s expectations (`entity`, `operation`, `id`/`keys`,
  `changed`, `timestamp`). Either match those names, or register a matching custom
  `INotificationPayloadDeserializer` via `options.PayloadDeserializer = ...`.
- **A hand-written shape passed to `envelope.ToTyped<T>()` that doesn't match the payload's
  top-level JSON.** It deserializes the *raw payload* directly into `T` with
  case-insensitive matching — a type built for the minimal payload (a bare `Id` property) will not
  deserialize correctly against the extended payload (which nests everything except
  entity/schema/table/operation under `keys`/`changed`), and vice versa. Match the type's shape to
  whichever payload builder the entity actually uses.

## Version mismatches

If the application's compiled model (and therefore its understanding of channel names/payload
shapes) was built against an older entity configuration than the trigger currently installed in
the database, notifications will arrive but may not deserialize as expected, or may arrive on a
channel nothing is listening to. This is exactly what `dotnet ef database update` is for — treat
notification trigger drift the same as any other schema drift: **the database schema (including
triggers) and the application's compiled model must come from the same migration.** There is no
runtime version negotiation; this library does not attempt schema versioning beyond what EF
Core's own migrations history already provides.

## Missing channels

`LISTEN <channel>` for a channel the listener doesn't know about simply never happens — there's
no error, because from the listener's perspective the channel doesn't exist until something asks
for it. Every channel has to be declared, by one of three routes:

- `options.AddNotificationMappingFromDbContexts()` (from `PgNotify.Runtime.EFCore`, called
  inside `AddPostgresNotifications(options => ...)`) reads
  them out of the models of the contexts that chained `UseNpgsqlNotifications()`, which is the only
  route where nothing is written by hand and nothing can drift from the generated triggers;
- `options.MapChannel<TEntity>("channel")` / `options.MapChannel("channel")`, for a process with no
  `DbContext` to read.

Registering a handler class is *not* enough on its own: a handler is keyed on an entity type, which
says nothing about which channel carries it. A handler that never runs is most often a channel
nobody declared — the resolved count is logged once at startup ("notification mapping resolved:
N channel(s)").

## Listener won't reconnect

- Check the connection string's credentials/network reachability independently (e.g. `psql
  "<same connection string>"`) — the listener's `ExponentialBackoffReconnectPolicy` will retry
  indefinitely by default, which looks identical to "stuck" from the health check's perspective if
  the underlying failure is persistent (wrong password, firewalled host, database doesn't exist).
- If you configured `ExponentialBackoffReconnectPolicy(maxAttempts: N)`, the listener stops
  retrying and the background hosted service task **faults** after `N` attempts — check your
  process's unhandled-exception/crash logs, not just the health check.
- A reconnect storm across many application instances after a shared outage (e.g. a database
  failover) is why the default policy includes up to 30% jitter — if you've overridden it with a
  custom `IReconnectPolicy`, make sure it still has some randomization for multi-instance
  deployments.

## Reconnect resets in-flight `Events<TEntity>()` subscriptions?

No — `Events<TEntity>()` subscriptions are independent of the underlying connection; they're backed by
`NotificationEventHub`, a singleton that outlives any individual `NpgsqlNotificationListener`
connection cycle. A reconnect only means a gap in notifications (anything published while
disconnected is lost — `LISTEN`/`NOTIFY` has no backlog/replay), not a broken subscription.

## Logical replication listener won't start: `wal_level` / permission errors

`AddPostgresLogicalReplication()` throwing (or the hosted service faulting) at startup with a
Postgres error about logical decoding or replication almost always means one of the two
server-level prerequisites `NotificationDeliveryMode.LogicalReplication` needs is missing — neither
is something a migration can set for you:

- **`ERROR: logical decoding requires wal_level >= logical`** — the server's `wal_level` is at its
  default (`replica`). Set `wal_level = logical` in `postgresql.conf` (or your managed Postgres
  provider's equivalent) and restart the server; this is a restart-requiring setting, a reload is
  not enough.
- **`ERROR: permission denied to start WAL sender` / `must have replication permission`** — the
  connection `PostgresLogicalReplicationOptions.ConnectionString` (or the `DbContext` connection
  `AddReplicationMappingFromDbContexts()` derived it from) authenticates as a role without the
  `REPLICATION` attribute. Grant it: `ALTER ROLE the_role WITH REPLICATION;`.

## A replication slot exists but nothing is being delivered

Check `pg_replication_slots` directly:

```sql
SELECT slot_name, active, wal_status, confirmed_flush_lsn FROM pg_replication_slots;
```

- **No row for the expected slot name** (`{NamePrefix}pgnotify_{ConsumerGroup}`, see
  `NotificationReplicationNames`) — the migration that creates it hasn't been applied, or the
  entity isn't actually configured for `NotificationDeliveryMode.LogicalReplication` (check
  `entityType.GetNotificationConfiguration()?.DeliveryMode`).
- **`active = false` for more than a moment** — nothing is currently streaming from it. Either the
  listener process isn't running, or it's stuck in `ExponentialBackoffReconnectPolicy`'s retry loop
  (same causes as [Listener won't reconnect](#listener-wont-reconnect) above — check logs for the
  same `Attempt`/reconnecting messages, just from `PgNotify.Internal.LogicalReplicationHostedService`
  instead of `NpgsqlNotificationListener`).
- **The table isn't actually in the publication.** `SELECT * FROM pg_publication_tables WHERE
  pubname = '<prefix>pgnotify_pub';` — if the table is missing, the migration that should have run
  `ALTER PUBLICATION ... ADD TABLE` either hasn't been applied, or the entity's `NamePrefix` changed
  without the corresponding migration being generated and applied (see the `NamePrefix` handling in
  `docs/plans/logical-replication-delivery.md`'s Phase 2 notes).

## An orphaned replication slot is growing WAL on the server

A slot nothing is consuming pins WAL indefinitely (`wal_status` moves from `reserved` toward
`extended`/`lost` the longer it sits unconsumed) — this is the sharper version of an orphaned
trigger, and this library never drops a slot automatically (see
`docs/plans/logical-replication-delivery.md`'s "Accepted costs"). Call
`database.FindOrphanedNotificationReplicationSlots()` (or the `Async` overload) to list slots
matching the naming convention that no entity in the current model asks for any more, then decide
by hand — via `SELECT pg_drop_replication_slot('slot_name')` — whether it's actually safe to drop
one (identification here is a naming-pattern match, not a hash-verified mark the way orphaned
trigger detection is, so double-check before dropping).
