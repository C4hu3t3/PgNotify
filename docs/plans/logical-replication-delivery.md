# Plan: logical replication as an opt-in delivery mode

Status: not started. No code written yet; this is the design to implement against.

## Background: what prompted this

`docs/architecture.md`'s Limits section is explicit that `LISTEN`/`NOTIFY` is fire-and-forget: a
notification fired while nobody is listening is lost, with no replay and no at-least-once
guarantee, and the library "does not attempt to paper over that gap." That is the right default —
see the non-goals at the top of the same file — but it leaves users who need durability with
nowhere to go inside the library at all.

A bespoke outbox table (trigger writes a row, a poller reads and dispatches it) was considered and
rejected before this plan: it duplicates data Postgres already durably has in its WAL, adds a
second write on every notified row, and reinvents cursor/consumer-group bookkeeping that Postgres
already provides for free through replication slots. Logical replication gives the same
at-least-once guarantee natively — a slot retains WAL until the consumer confirms an LSN, so a
disconnected listener loses nothing and resumes exactly where it left off on reconnect — with no
new table and no extra write-path cost beyond what replication itself costs.

The API surface below was confirmed by loading `Npgsql` 10.0.3 (already pinned in
`Directory.Packages.props`) via reflection, not from memory:

- `LogicalReplicationConnection.CreatePgOutputReplicationSlot(name, temporary, snapshotInitMode, twoPhase, ct)`
  → `PgOutputReplicationSlot` (`SnapshotName`, `ConsistentPoint`).
- `PgOutputConnectionExtensions.StartReplication(connection, slot, PgOutputReplicationOptions{PublicationNames, ...}, ct, resumeFromLsn?)`
  → `IAsyncEnumerable<PgOutputReplicationMessage>`.
- `ReplicationConnection.SetReplicationStatus(lsn)` / `SendStatusUpdate(ct)` / `WalReceiverStatusInterval`
  confirm progress back to the server.
- Messages: `RelationMessage` (schema), `InsertMessage`/`DefaultUpdateMessage`/`FullUpdateMessage`/
  `IndexUpdateMessage`/`FullDeleteMessage`/`KeyDeleteMessage`, `BeginMessage`/`CommitMessage`.
  `FullUpdateMessage.OldRow` only exists under `REPLICA IDENTITY FULL`.
- `ReplicationTuple : IAsyncEnumerable<ReplicationValue>` — must be drained in column order before
  moving to the next message; nothing can be deferred.

A replication slot only ever supports one active stream. That resolves the consumer-model question
for free: one slot is a single competing-consumer group, and fan-out across independent services is
just multiple named slots against the same publication — Postgres tracks each slot's confirmed LSN
independently, so no cursor table is needed on our side either.

## Non-goals for v1

- No `TRUNCATE` handling (no `OnTruncate` exists today; `TruncateMessage` is logged and skipped).
- No two-phase-commit messages (`PrepareMessage`/`CommitPreparedMessage`/...).
- No automatic slot drop on migration removal (see Phase 2 — dropping a slot is destructive and
  gets its own safety gate, not a silent DDL diff).
- No PG15+ publication column lists or row filters — scope this to whole-row, whole-table
  replication first.
- No provider other than Npgsql; this whole mode simply doesn't exist for other providers, same as
  the trigger path today.

## Phase 0 — spike: prove the primitives against `postgres:16-alpine`

Nothing about replication has been exercised in this repo yet, unlike triggers, which are already
proven end to end. Before touching the model/annotation layer, confirm against the same
Testcontainers image `PgNotify.IntegrationTests` already uses:

- `postgres:16-alpine`'s default `wal_level` is `replica`, not `logical` — confirm the container
  needs a custom command (`postgres -c wal_level=logical`) or config file, and that this is a
  restart-requiring setting (not something `ALTER SYSTEM` + reload alone fixes).
- A non-superuser role with `REPLICATION` (via `ALTER ROLE ... REPLICATION` or `pg_read_all_data` +
  the attribute) can open a `LogicalReplicationConnection` (connection string needs
  `replication=database`) and stream from a `pgoutput` slot.
- `SELECT pg_create_logical_replication_slot('x', 'pgoutput')` works over an ordinary (non
  -replication) connection — i.e. slot creation can live in normal migration SQL, only the
  streaming consumer needs the special connection mode.
- Killing the streaming connection mid-stream and reopening `StartReplication` with no
  `resumeFromLsn` argument resumes from the slot's own `confirmed_flush_lsn` — i.e. the runtime
  does not need to persist any position itself; Postgres already has it. Confirm this explicitly,
  since it removes an entire category of client-side state this plan would otherwise need.

Spike lives as a throwaway console app under the scratchpad, not committed. Record the answers
here (or in Phase 3) once run — if any assumption above is wrong, later phases need to change
accordingly.

### Findings (run against `postgres:16-alpine`, custom command `-c wal_level=logical`)

All four assumptions confirmed, plus one correction that changes Phase 3's confirmation logic
from "should probably" to "must, or durability silently breaks":

1. `wal_level=logical` takes effect at bootstrap via a container command override; no separate
   restart step needed in a fresh container (an existing cluster would need one — not exercised
   here).
2. A non-superuser role created with plain `... REPLICATION` opened a `LogicalReplicationConnection`
   and streamed successfully. Note for Phase 3: the connection string must **not** include a
   `Replication=...` keyword — Npgsql's `NpgsqlConnectionStringBuilder` does not recognize that
   keyword at all (`ArgumentException: Couldn't set replication`); `LogicalReplicationConnection`'s
   constructor sets the replication mode itself once opened. `NotificationConnectionString`'s new
   transform is therefore *not* additive in the way originally planned — it should not add a
   `replication=database` pair, since doing so breaks the connection string outright.
3. `SELECT pg_create_logical_replication_slot('name', 'pgoutput')` works over an ordinary
   connection, confirming slot creation belongs in normal migration SQL as planned.
4. Reconnecting via `StartReplication` with no `resumeFromLsn` argument resumes from the slot's own
   `confirmed_flush_lsn` with no client-side persisted state — confirmed by inserting a row while no
   consumer was attached, then reconnecting and observing only that row replay, not an earlier
   already-confirmed one.

**Correction found via the same run:** confirming `SetReplicationStatus`/`SendStatusUpdate` using
an `InsertMessage`'s `WalEnd` and then disconnecting **before** the matching `CommitMessage` does
not move the server's resumption point past that transaction at all — reconnecting replayed the
entire transaction from `BeginMessage` again. Confirming using the `CommitMessage`'s `WalEnd`
instead resumed correctly, skipping the fully-confirmed transaction and only redelivering what
came after. Logical decoding is transaction-granular: there is no such thing as resuming
mid-transaction. Phase 3's "confirm only after a commit's notifications have all been dispatched"
was already the plan; this proves it is not merely the safer choice but the *only* one that works —
confirming per-row/per-message would silently never advance the resumption point for anything but
single-statement transactions, which is exactly the kind of gap that would go unnoticed until a
multi-row transaction hit a restart.

Also observed: `pg_replication_slots.wal_status = reserved` on an idle, unconsumed slot — direct
confirmation that an orphaned slot pins WAL as described in the Limits/Accepted-costs sections
below, worth surfacing in the orphan-slot warning's message text in Phase 2.

## Phase 1 — configuration surface

- `PgNotify.Core`: `NotificationDeliveryMode { Notify (default), LogicalReplication }`.
- `PgNotify.EFCore/NotificationOptionsBuilder.cs`: `WithDelivery(NotificationDeliveryMode)`, and
  `[NotifyChanges(Delivery = ...)]` on the attribute, so both configuration styles stay convergent
  on `NotificationConfigurationWriter.Apply(...)` per the existing single-write-path rule in
  `CLAUDE.md`.
- New primitive annotations under `NotificationAnnotationNames`: `DeliveryMode` (string enum),
  `ReplicaIdentityFull` (bool, opt-in — only entities that need old-value-aware filtering pay the
  WAL cost), `ReplicationConsumerGroup` (string, defaults to the `DbContext`'s own name — feeds
  slot naming for the fan-out-via-named-slots model above).
- `NotificationValidationConvention`:
  - reject `OnUpdate(x => new {...})` (property filtering) under `LogicalReplication` unless
    `ReplicaIdentityFull` is also set — without it there is no old row to compare against, so the
    filter cannot be evaluated client-side and would either silently no-op or silently fire on
    every update; fail fast at model-build time instead, same posture as every other check in this
    convention.
  - `RejectSharedTable`/`RejectUnmappedToTable` apply unchanged — `RelationMessage` still resolves
    at table granularity, same ambiguity as `table.EntityTypeMappings.First()` today.

## Phase 2 — migrations DDL (implemented, deviates from the plan in three ways)

Built as `NotificationReplicationSqlBuilder`/`NotificationReplicationFingerprint`
(`src/PgNotify.Migrations/Internal/`), wired into `NpgsqlNotificationsAnnotationProvider`,
`NpgsqlNotificationsMigrationsAnnotationProvider`, and `NpgsqlNotificationsMigrationsSqlGenerator`
as an independent second fingerprint channel alongside the trigger one (both checked on every
`Generate(...)` overload; a table's two fingerprints are mutually exclusive per the one-delivery
-mode-per-entity rule, which is what makes a Notify↔LogicalReplication switch in one migration
correct by construction — one channel's fingerprint disappears, the other's appears, no
coordination code needed between them). Verified two ways: 87 unit tests asserting exact generated
SQL text (`NotificationsReplicationSqlGenerationTests.cs`), and the generated DDL executed twice
(idempotency) against a real `postgres:16-alpine` — including the shared-slot-across-two-tables
case, and a removal that correctly left a still-referencing table's slot and the other table's
publication membership untouched.

Deviations from the original plan text below, kept for the reasoning:

- **Idempotency mechanism.** The plan didn't commit to a specific SQL shape. Landed on `DO $$ ...
  IF NOT EXISTS (catalog check) THEN EXECUTE '...' END IF; END $$` for `CREATE PUBLICATION` and
  `ALTER PUBLICATION ... ADD/DROP TABLE` (PostgreSQL has no `IF NOT EXISTS` for either), plain
  unconditional SQL for `ALTER TABLE ... REPLICA IDENTITY` (re-setting the same value is already a
  no-op, no guard needed), and the `WHERE NOT EXISTS (SELECT ... pg_replication_slots)` guard the
  Phase 0 spike already used for the slot. Publication-membership existence is checked via
  `to_regclass('<delimited-and-escaped-identifier>')` against `pg_publication_rel`/`pg_publication`
  rather than a `schemaname`/`tablename` text match against `pg_publication_tables`, so schema
  resolution for an unqualified table name is delegated to PostgreSQL itself (the same resolution
  `ALTER PUBLICATION ... ADD TABLE` on that same identifier would use) instead of the generator
  guessing "public".
- **No database-first support in v1.** `EnsureNotificationTriggersAsync`/
  `GenerateNotificationTriggersScript` have no logical-replication counterpart — out of scope here,
  not attempted.
- **Orphan detection is read-only in v1, and is a naming-pattern match, not a hash-verified mark.**
  `OrphanedNotificationReplicationSlotReader` + `DatabaseFacade.FindOrphanedNotificationReplicationSlots()`/
  `...Async()` (mirroring the trigger case's `Find...`, deliberately with no `Remove...` counterpart)
  scan `pg_replication_slots` for `plugin = 'pgoutput'` and a `pgnotify_`-containing name, then diff
  against the slot names the current model's `LogicalReplication` entities would produce. Unlike
  `OrphanedNotificationTrigger`, there is no `COMMENT ON`-equivalent mechanism for a replication
  slot to carry a verifiable fingerprint, so this is a heuristic and says so in its own doc comment.
  `OrphanedNotificationReplicationSlot` surfaces `WalStatus` from the catalog specifically so a
  caller can see how much WAL retention is actually at stake before deciding anything.

## Phase 2 — original plan text (superseded by "implemented" above; kept for the reasoning trail)

New provider path parallel to the existing trigger one (`NpgsqlNotificationsAnnotationProvider` /
`NpgsqlNotificationsMigrationsSqlGenerator`), scoped to entities with `DeliveryMode ==
LogicalReplication`:

- `CREATE PUBLICATION {prefix}pgnotify_pub FOR TABLE ...` — one publication per `NamePrefix` scope
  (mirrors existing trigger/function naming), diffed via `ALTER PUBLICATION ... ADD/DROP TABLE`.
- `ALTER TABLE ... REPLICA IDENTITY FULL` for entities with `ReplicaIdentityFull = true`, `DEFAULT`
  otherwise (Postgres's own default — no-op if never changed).
- `SELECT pg_create_logical_replication_slot('{prefix}pgnotify_{consumerGroup}', 'pgoutput') WHERE
  NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = ...)` — idempotent creation,
  one slot per distinct `ReplicationConsumerGroup` value seen in the model.
- Fingerprint: extend the existing "hash the generated SQL, not the config" approach
  (`NotificationFingerprint`) with a second, independent fingerprint for the replication DDL, so a
  fix to this SQL builder regenerates for users the same way a trigger SQL fix already does.
- Removal: mirror the two-hook pattern documented in `CLAUDE.md` for trigger removal
  (`NpgsqlNotificationsAnnotationProvider.For(ITable, designTime)` for `AlterTableOperation.OldTable`,
  `NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove(ITable)` for `DropTableOperation`) —
  needed here for `ALTER PUBLICATION ... DROP TABLE`.
- **Slot drop is deliberately not automatic.** Dropping a slot permanently discards its confirmed
  position and, if done wrong, can discard unconsumed WAL. When a replication-configured entity's
  config is removed, emit a warning (extending the orphaned-trigger detection from `37ee87f`) that
  names the now-orphaned slot rather than a `DROP` statement — an unconsumed slot grows WAL
  unboundedly, which is worse than an orphaned trigger, but a wrongly-dropped slot is worse still.
  An explicit `EnsureReplicationSlotsRemovedAsync(...)`-style opt-in helper, analogous to the
  database-first `EnsureNotificationTriggersAsync()`, can do the actual drop later if this turns
  out to be needed — out of scope for v1.

## Phase 3 — runtime consumer

New project `PgNotify.Runtime.Replication` (references `PgNotify.Runtime` only, plus `Npgsql`
directly for the replication types) — kept separate from `PgNotify.Runtime` so a NOTIFY-only
listener never pulls in replication connection-mode complexity, same layering reason
`Runtime.EFCore` is its own project.

- `PgNotify.Runtime.EFCore` gains the table→entity/column mapping needed here (which tables are
  replication-configured, their consumer group, their `ReplicaIdentityFull`/filtered-column state)
  exposed through the existing `INotificationMappingSource` extension point (or a sibling
  interface in `PgNotify.Runtime` if the shapes don't fit) — so `Runtime.Replication` stays free of
  any EF Core dependency, same as `Runtime` itself today.
- `NotificationConnectionString`: new transform (name TBD, e.g. `ForReplication`) — per the Phase 0
  finding, this must **not** add a `replication=...` keyword (Npgsql's connection string builder
  rejects it outright; `LogicalReplicationConnection` sets replication mode itself once opened).
  Its actual job is the same kind of correction `ForListening` already does — strip pooling and
  multiplexing, since a replication connection is never pooled or multiplexed either — just without
  the keyword addition originally assumed here.
- Hosted service loop: `Open` → `IdentifySystem` → `StartReplication(slot, options, ct)` with no
  `resumeFromLsn` (per the Phase 0 spike finding — Postgres already knows where to resume). Cache
  `RelationMessage`s by `RelationId`, drain tuples into column-name→value, filter changed columns
  against the entity's watched-columns config when `OldRow` is available, build a
  `NotificationEnvelope` directly from decoded values (no JSON round-trip — also sidesteps the
  8000-byte `NOTIFY` payload cap entirely for entities on this mode), and push it into the
  **existing** `NotificationDispatchPipeline` unchanged — this is the point of reusing the runtime
  rather than building a second dispatch stack.
- Confirm `SetReplicationStatus`/`SendStatusUpdate` only after a `CommitMessage`'s notifications
  have all been dispatched, never mid-transaction — preserves "confirmed" meaning "this whole
  transaction's effects were handled," and keeps the same at-least-once/idempotent-handler
  contract `RetryNotificationMiddleware` already established for the NOTIFY path.
- Reconnect: reuse `IReconnectPolicy` if its shape fits a replication connection's open/stream loop
  without forcing something replication-specific through it; otherwise a second implementation.
  Decide during implementation, not in this plan.
- `TruncateMessage` → log at warning level and skip; explicitly not silently ignored.

## Phase 4 — tests and docs

- `PgNotify.Migrations.Tests`: exact-SQL-text assertions for publication/replica-identity/slot DDL,
  same style as `MigrationTestHelper`-driven trigger tests; fingerprint round-trip test mirroring
  the existing one.
- `PgNotify.EFCore.Tests`: validation convention rejects filtered `OnUpdate` without
  `ReplicaIdentityFull`; attribute/fluent convergence test for `Delivery`/`WithDelivery`, mirroring
  the existing convergence tests for other options.
- `PgNotify.IntegrationTests`: requires reconfiguring the shared Testcontainers Postgres fixture for
  `wal_level=logical` and a `REPLICATION`-capable role (Phase 0 answers exactly what that needs to
  look like) — insert/update/delete against a replication-configured entity and assert handler
  invocation; kill the streaming connection mid-stream and assert a restart redelivers without loss
  and without needing any client-persisted position.
- `docs/performance.md`: measure `REPLICA IDENTITY FULL` WAL overhead the same way trigger overhead
  is already measured there.
- `docs/architecture.md`: add a subsection under Limits describing this mode as the explicit,
  deliberate escape hatch for the documented durability gap — same directness as the existing
  bullets, listing `wal_level=logical`, the `REPLICATION` role requirement, orphaned-slot WAL
  growth, `REPLICA IDENTITY FULL` cost, and the missing `TRUNCATE` handling.
- `docs/troubleshooting.md`: slot not advancing, orphaned slot growing WAL, "logical decoding
  requires wal_level >= logical" errors.
- `README.md`: mention as an advanced, explicitly opt-in feature — not in the quickstart.

## Accepted costs

- `wal_level=logical` and the `REPLICATION` role attribute are server-level/operational
  prerequisites entirely outside migrations' control — the library can validate and fail fast
  (e.g. check `SHOW wal_level` at listener startup) but cannot provision them.
- Enabling this on an entity is a second, independent write path from the trigger one; an entity
  cannot cheaply have both without accepting either double bookkeeping or picking one mode as
  authoritative — v1 makes delivery mode a single choice per entity, not a per-entity combination.
- Everything in Non-goals above is a real, user-visible gap in v1, not an oversight to be silently
  patched later without a release note.
