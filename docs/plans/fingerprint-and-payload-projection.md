# Plan: SQL-derived fingerprint, payload projection, overflow guard

Status: all four phases implemented on `fingerprint-from-generated-sql`.
Phases 0 and 2 ship together, as one regeneration for users rather than two.

## Background: what prompted this

Two configuration styles pick different payload defaults — `[NotifyChanges]` defaults to the
minimal payload, `HasDatabaseNotifications()` to the extended one. Under the extended payload the
row key lives inside `"keys"`, so a `record XInserted(int Id)` (the shape the source generator
emits, and the shape the docs use) binds `Id` to `0`: `ToTyped<T>()` deserializes the raw payload
as-is, and there is no top-level `"id"` to match. Verified end to end against `postgres:16-alpine`:
the event is routed correctly, only the key is missing.

That is a symptom of a wider problem — the typed event's shape is implied by a configuration
default rather than stated. Phase 1 addresses it by letting the payload shape be declared
explicitly.

## Phase 0 — compute the fingerprint from the generated SQL

### The defect

`NpgsqlNotificationsMigrationsSqlGenerator.Generate(AlterTableOperation, …)` short-circuits when
`newFingerprint == oldFingerprint`. The fingerprint is therefore the only gate: anything it does
not cover is never regenerated. `NotificationFingerprint.Compute` currently derives it from the
*configuration* (operations, watched columns, channel names, payload fields), with
`allTableColumns` deliberately left `null` on the migrations path.

Two consequences, the first of which is a live bug:

1. **Adding a mapped column to an entity with an unfiltered `OnUpdate()` does not regenerate the
   trigger.** The diff emits only `ALTER TABLE … ADD`. The function keeps its previous
   `IS DISTINCT FROM` guard and `changed` array, so an `UPDATE` touching only the new column never
   notifies — even though unfiltered `OnUpdate()` is documented as watching every mapped column.
2. **A fix to the SQL builder never reaches users.** If the generated SQL changes but the user's
   configuration does not, the fingerprint is unchanged and no migration is produced.

### The change

`NotificationFingerprint.Compute` stops describing the configuration and instead hashes the SQL
that `NotificationTriggerSqlBuilder.BuildUpsertStatements(config, allTableColumns)` actually
produces. The fingerprint then covers, by construction, everything that influences the generated
SQL — including changes made inside this library.

- `NpgsqlNotificationsAnnotationProvider.For(ITable table, …)` already receives the table, so
  `table.Columns` supplies `allTableColumns` on the migrations path too. This removes the current
  asymmetry with the database-first path, which already passes the full column list.
- **Spike result: resolved.** Injecting `ISqlGenerationHelper` as a second constructor parameter of
  `NpgsqlNotificationsAnnotationProvider` is resolved by EF Core's internal container with no
  cycle; the full non-integration suite (172 tests) passes with it in place. The fallback of a
  hand-bumped `GeneratorVersion` constant is not needed — and is worse, because it can be
  forgotten.
- Hash the SQL with **line endings removed, and nothing else touched**. This is required for
  correctness, not cosmetics: the generated text's line endings vary by environment, from two
  independent sources. `NotificationTriggerSqlBuilder` uses both `StringBuilder.AppendLine` (which
  follows `Environment.NewLine`, so `\r\n` on Windows) and raw string literals (which carry the
  source file's own line endings — verified: a `.cs` file checked out with CRLF yields `\r\n`
  inside the literal). Left untouched, a Windows developer and a Linux CI compute different
  fingerprints for the same model, producing phantom migrations that ping-pong between teammates.

  Removed outright rather than normalized to `\n`, so the result does not depend on the
  repository's git configuration at all. A `.gitattributes` pinning `*.cs` to LF would fix the
  checkout half of the problem, but only for clones that actually have it — a runtime rule that
  needs no repository setup is the stronger guarantee.

  Do **not** collapse runs of spaces or strip indentation. The whole function body sits inside a
  dollar-quoted literal, and channel names, JSON keys, and entity display names are single-quoted
  literals: collapsing whitespace would hash `WithChannelName("my channel")` and
  `WithChannelName("my  channel")` identically, reintroducing exactly the missed regeneration this
  phase exists to eliminate. The equivalent exposure for line endings — a channel or table name
  containing a literal newline — is not sanitized by anything in the API but is not produced by any
  real configuration.

### Accepted cost

Any release that changes the generated SQL now produces a migration for every user at their next
`dotnet ef migrations add`. That is the intended behavior — it is how a fix propagates — but it
belongs in the release notes.

### Also required

- Delete the paragraph in `NotificationFingerprint.cs` documenting cross-version textual stability
  as a guarantee ("the migrations-path callers rely on this exact text staying stable across
  versions"), and the corresponding passage in `CLAUDE.md`. They encode the opposite intent.
- The database-first path (`EnsureNotificationTriggersAsync`) stores `ComputeHash` in a
  `COMMENT ON FUNCTION` and compares it on the next call, so it starts self-healing on upgrade.

### Tests

- `Migrations.Tests`: adding a column to an unfiltered `OnUpdate()` entity must emit
  `CREATE OR REPLACE FUNCTION`, with the new column present in both the guard and `changed`.
- `Migrations.Tests`: equal generated SQL implies equal fingerprint, different SQL implies a
  different one — assert the property, not a frozen value, so intentional changes do not
  manufacture failures.
- `IntegrationTests`: add a column, apply the diff, `UPDATE` that column alone, assert the event
  arrives. This test fails before the fix.
- `IntegrationTests`: the database-first path reinstalls the function when the generated SQL
  changes.

## Phase 1 — `WithPayload(x => new { … })`

Lets the payload shape be declared explicitly, so a typed event's shape is stated rather than
inherited from a default.

1. `PgNotify.Core/Payloads/NotificationPayloadBuilderContext.cs` — add
   `IReadOnlyList<string> PayloadColumns`. Payload builders are reconstructed from annotations
   (kind + type name) and must stay stateless, so the projected columns travel through the
   context, exactly as `WatchedUpdateColumns` already does.
2. `PgNotify.Core/Payloads/ProjectedNotificationPayloadBuilder.cs` (new) — emits
   `Constant("entity")`, `Operation`, one `NotificationPayloadField.Column` per projected column,
   **plus the key unconditionally** (`id` when single-column, `keys` otherwise) even when the user
   did not select it. Without that, a payload can fail to identify its row, which is the trap this
   phase exists to close.
3. `PgNotify.EFCore/Metadata/NotificationAnnotationNames.cs` — add `PayloadColumns` (CSV
   string, keeping the primitives-only rule) and `NotificationPayloadBuilderKind.Projected`.
4. `PgNotify.EFCore/Metadata/NotificationConfigurationWriter.cs` — new `payloadColumns`
   parameter; update both callers (`NotificationOptionsBuilder.Save()` and
   `NotifyChangesAttributeConvention`, which passes `[]`).
5. `PgNotify.EFCore/NotificationOptionsBuilder.cs` — add
   `WithPayload(Expression<Func<TEntity, object?>>)`, reusing `PropertySelectorExpressionHelper`
   (already backing `OnUpdate`). No overload ambiguity: a lambda does not convert to
   `INotificationPayloadBuilder`.
6. `PgNotify.EFCore/EntityTypeNotificationExtensions.cs` — decode the annotation and resolve
   property names to column names via `GetColumnName(storeObject)`, mirroring `watchedColumns`.
7. `PgNotify.EFCore/Conventions/NotificationValidationConvention.cs` — reject navigations and
   unmapped properties in the payload selector, reusing the watched-columns checks.
8. `PgNotify.Migrations` — no change. `NotificationPayloadFieldKind.Column` is already
   handled and `BuildCreateOrReplaceFunctionSql` already resolves `NEW`/`OLD` per operation.
9. `PgNotify.Analyzers` — extend PGN002 to the payload selector.

Note: the source generator is attribute-driven and cannot see fluent projections, so projected
entities keep hand-written event types — unchanged from today.

Open design point, deliberately left out of scope: `UPDATE` projects `NEW` values only. Old values
would need something like an `includeOld:` option; the signature should leave room for it rather
than foreclose it.

## Phase 2 — payload overflow guard

Measured on `postgres:16-alpine`: `pg_notify` accepts 7999 **bytes** and raises SQLSTATE 22023
("payload string too long") at 8000. The limit is on bytes, not characters —
`repeat('é', 4000)` fails. The error surfaces inside the trigger, so it aborts the user's write,
not merely the notification.

Use a size check rather than a PL/pgSQL `EXCEPTION` block: `BEGIN … EXCEPTION` opens a
subtransaction on every invocation, paid on every write for a case that almost never happens,
whereas a comparison is cheap, deterministic, and does not swallow unrelated errors.

```sql
DECLARE payload text;
…
payload := (json_build_object(…))::text;
IF octet_length(payload) > 7999 THEN
    payload := (json_build_object('entity', …, 'operation', …, 'id', NEW."Id",
                                  'truncated', true))::text;
END IF;
PERFORM pg_notify('channel', payload);
```

The `truncated` flag is required: without it a consumer cannot distinguish a reduced payload from
a normal one and does not know it must re-read the row.

**Decided: guard every payload shape, not just `Projected`.** The risk is not confined to
projections — the extended payload can overflow through a long `changed` array on a wide table
(PostgreSQL allows 1600 columns) or through a text primary key landing in `keys`. Scoping the guard
to `Projected` would protect whoever projects two `int`s while leaving the default extended payload
unprotected on a 400-column table, and the unprotected failure mode is the worst one in the system:
the user's write is aborted, not just the notification.

The cost of the wider scope — a migration for every user — is zero **provided this ships in the same
release as phase 0**, which already forces one regeneration for everyone. Sequencing them apart
would double it.

Two consequences to accept and document: every consumer's payload contract becomes "full shape, or
reduced shape with `truncated: true`", and the guarantee is not absolute — a primary key that alone
exceeds 7999 bytes overflows even the fallback. Teams that would rather fail hard than receive a
reduced payload (CDC, audit pipelines) get an explicit opt-out, `WithPayloadOverflow(Fail | Truncate)`,
defaulting to `Truncate`.

No fingerprint work is needed here: the guard changes the generated SQL, so after Phase 0 it
changes the hash automatically.

Integration test: insert a row with a 10 kB text column, assert the `INSERT` succeeds and the
event arrives with `truncated: true`. This is the most important test of the phase — it is the one
that proves user writes are not broken.

## Phase 3 — payload kind enum

`public enum NotificationPayloadKind { Minimal, Extended }` in Core — two members, not three:
`Custom` stays carried by `WithPayload<TBuilder>()`, since it cannot be selected without a type.
Add `WithPayload(NotificationPayloadKind)` and keep `WithMinimalPayload()`/`WithExtendedPayload()`
as `[Obsolete]` shims delegating to it, so nothing breaks.

Cosmetic on its own; it is worth doing only alongside Phase 1, so the `WithPayload` family reads
consistently.


## Phase 0 implementation notes (done)

- `NotificationFingerprint.Compute`/`ComputeHash` now take the generated statements instead of an
  optional `allTableColumns` list; the `allTableColumns` asymmetry between the migrations and
  database-first paths is gone, since both now hash the SQL they would actually run.
- `ISqlGenerationHelper` is injected into both `NpgsqlNotificationsAnnotationProvider` and
  `NpgsqlNotificationsMigrationsAnnotationProvider`; EF Core's internal container resolves it with
  no extra registration.
- Line endings are stripped, not normalized, by `NotificationFingerprint.StripLineEndings`, so the
  hash depends on nothing outside the process. No `.gitattributes` was added.
- `ForRemove` computes the fingerprint the same way as everywhere else even though
  `DropTableOperation` only tests the annotation for presence — which annotations EF Core compares
  is not an assumption worth encoding locally.
- Regression coverage: `PgNotify.Migrations.Tests/NotificationRegenerationTests.cs` (column
  added under an unfiltered `OnUpdate()` regenerates; an explicit watch list does not; an unchanged
  model produces no diff; a payload-only change still regenerates) and
  `PgNotify.IntegrationTests/MigrationDiffRegenerationTests.cs` (second migration adds a
  column, `UPDATE` touching only that column must notify). Both fail with the fix reverted.


## Phase 2 implementation notes (done)

- `NotificationPayloadOverflow` (Core) + `WithPayloadOverflow(...)` + a `Notifications:PayloadOverflow`
  annotation, defaulting to `Truncate` — including for models written before the option existed,
  which must get the safe behavior rather than the historical one.
- `NotificationEnvelope.Truncated` surfaces the flag so a handler can react without re-parsing
  `RawPayload`.
- The fingerprint needed no work at all: switching `Truncate`/`Fail` changes the generated SQL, so
  the phase 0 hash carries it into the diff. `PayloadOverflowTests.Changing_the_overflow_behavior_regenerates_the_trigger`
  locks that in.
- **Correction to the phase 2 rationale.** The argument for guarding every payload shape assumed a
  large row could overflow the extended payload. It cannot: the extended payload carries only
  metadata (keys, changed *column names*, timestamp), never column values. The reachable overflow
  paths are a payload that carries row data (a custom `INotificationPayloadBuilder` today, phase 1's
  projection tomorrow), a very wide table's `changed` array, and a text primary key — and the last
  one defeats the fallback too, since the key is what the reduced payload is made of. The uniform
  guard is still the right call, because it costs one comparison and the shapes that *can* overflow
  are exactly the ones users reach for, but it protects the default extended payload far less often
  than the decision assumed.
- The integration test therefore uses a custom builder that puts a column value in the payload —
  the shape phase 1 will generate — rather than relying on row size alone.


## Phases 1 and 3 implementation notes (done)

- `NotificationPayloadBuilderContext` gained `PayloadColumns` as a positional parameter rather than
  an optional one, so a builder can never silently receive an empty projection; the two internal
  construction sites and the Core payload tests were updated.
- `ProjectedNotificationPayloadBuilder` emits the key unconditionally, and skips it in the selected
  columns when the selector names it too — otherwise `WithPayload(x => new { x.Id, x.Name })`
  produces a duplicate JSON member.
- Validation lives in two places on purpose, matching how watched columns are already handled:
  `NotificationValidationConvention` throws at model-build time, and the new `PGN004` reports the
  same mistake at edit time. PGN002 was left alone rather than generalized — its message is about
  `IS DISTINCT FROM`, which says nothing about a payload.
- Phase 3's `[Obsolete]` shims are real deprecations, so every call site in the repo was migrated
  to `WithPayload(NotificationPayloadKind.…)` to keep the build at zero warnings; one test still
  exercises the shims behind `#pragma warning disable CS0618`.
- The source generator still only sees `[NotifyChanges]`, so projections keep needing hand-written
  event types. Teaching it to read fluent configuration would mean re-implementing the EF Core
  model builder at compile time, which is out of scope by design.

## Test coverage note

Only `PgNotify.IntegrationTests` runs against a real server (Testcontainers,
`postgres:16-alpine`); every other project asserts generated SQL as *text* and never opens a
connection — verified by pointing `MigrationTestHelper` at an unroutable host, which changes
nothing. Text assertions cannot catch invalid SQL, so each generated shape needs a server
somewhere:

- Executed: minimal and extended payloads, filtered and unfiltered `OnUpdate`, delete, name
  prefixes, the overflow guard in both the plain and the nested (update) branch, projections on
  insert/update/delete, composite-key projections, and the database-first path.
- Channel strategies (`topic`, `single`, `WithChannelName`) are now executed too. The shared-channel
  case earned its place by mutation: making `NotificationTypeRegistry.Find` key on channel and
  operation alone, dropping the entity, leaves all 206 unit tests green — including the six that
  target the registry directly — and is caught only by the integration test.


## Follow-up: naming conventions (done)

A projection's JSON keys were initially the column names, which silently unbinds every member of
the event type as soon as a column is renamed — by `HasColumnName`, or across the whole model by
`EFCore.NamingConventions`. Measured with the real package (10.0.1): under
`UseSnakeCaseNamingConvention()` the payload emitted `'internal_note'`, and `ToTyped<T>()`'s web
defaults match case-insensitively but not across underscores, so `InternalNote` stayed null.

`NotificationPayloadColumn` now carries the property name and the column name separately: the
trigger reads the column, the JSON key is the property. Verified by mutation — keying on the column
again makes `SnakeCaseNamingTests` fail while everything else stays green.

Two things that turned out **not** to be problems, both measured rather than assumed:

- The channel is derived from the model, so it follows any naming convention correctly
  (`order_lines` under snake_case), as do the generated trigger and function names.
- `EFCore.NamingConventions` does not rewrite explicitly configured names: `ToTable("Product")` and
  `HasColumnName("MyExplicitColumn")` survive it, only convention-derived names change. The
  `[Table("...")]` discipline the `CacheInvalidation.WebApi` sample documents for source-generated
  entities therefore still holds under the package.
