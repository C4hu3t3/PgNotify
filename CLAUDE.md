# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A PostgreSQL `LISTEN`/`NOTIFY` integration for EF Core: entities configure notifications via
`[NotifyChanges]` or `.HasDatabaseNotifications(...)`, EF Core migrations generate the
trigger/trigger-function DDL automatically, and a runtime listener routes raw `NOTIFY` payloads to
handlers keyed on the entity types the model already describes. See `README.md` and `docs/architecture.md` for the full design
rationale; this file is about working *in* the codebase, not the feature set.

## Commands

```bash
# Build everything
dotnet build

# Run every test project except the Docker-backed integration tests
dotnet test --filter "FullyQualifiedName!~IntegrationTests"

# Run a single test project
dotnet test tests/PgNotify.Migrations.Tests/PgNotify.Migrations.Tests.csproj

# Run a single test (any project)
dotnet test tests/PgNotify.EFCore.Tests/PgNotify.EFCore.Tests.csproj --filter "FullyQualifiedName~WithNamePrefix_sets_the_entity_level_prefix"

# Run the Testcontainers-based integration tests (needs a running Docker daemon; pulls postgres:16-alpine)
dotnet test tests/PgNotify.IntegrationTests/PgNotify.IntegrationTests.csproj

# Run the sample end to end
cd samples/CacheInvalidation.WebApi
docker compose up -d
dotnet run

# Run all PgNotify.Runtime micro-benchmarks (BenchmarkDotNet; always -c Release)
dotnet run -c Release --project benchmarks/PgNotify.Benchmarks

# Run one benchmark class/method (glob against Namespace.Class.Method)
dotnet run -c Release --project benchmarks/PgNotify.Benchmarks -- --filter "*DispatchBenchmarks*"
```

Regenerating the sample's migration (needs the `dotnet-ef` tool: `dotnet tool install --global dotnet-ef`):

```bash
cd samples/CacheInvalidation.WebApi
dotnet ef migrations add <Name> --context SampleDbContext -o Migrations
```

A useful sanity check after touching anything in `PgNotify.Migrations`: run
`dotnet ef migrations add ProbeForChanges` in the sample and confirm the generated migration is
**empty** (proves nothing about the annotation/fingerprint model introduced a spurious diff), then
delete the probe migration files instead of `dotnet ef migrations remove` (which needs a live DB
connection to check applied migrations).

There is no separate lint step; `Directory.Build.props` sets `WarningsAsErrors` on nullable
warnings and `EnableNETAnalyzers`, so `dotnet build` is the lint check.

## Solution layout and why

Central Package Management (`Directory.Packages.props`) pins all versions; `Directory.Build.props`
sets nullable and `RootNamespace = PgNotify` for every project **except**
`samples/CacheInvalidation.WebApi`, which overrides `RootNamespace` back to its own name in its own
`.csproj` — without that override, `dotnet ef migrations add` derives the wrong `Migrations/`
namespace from the solution-wide default. Target framework is `net10.0` by default; every packable
`src/` library (everything except `PgNotify.Analyzers`) and every `tests/` project instead
multi-target `net10.0;net11.0`, so the published packages support both the current LTS runtime and
the next preview one, and `dotnet test` actually runs the suite on both rather than only compiling
against them. `.github/workflows/build.yml` installs both SDKs (`include-prerelease: true` for
.NET 11) for exactly this reason — building with only the .NET 10 SDK fails with `NETSDK1045` on
every `net11.0` target.

- `PgNotify.Core` — provider-agnostic vocabulary (channel-naming strategies, payload
  contracts, `NotificationEnvelope`). No EF Core, no Npgsql dependency at all.
- `PgNotify.EFCore` — `[NotifyChanges]`, `HasDatabaseNotifications()`, the annotation-backed
  configuration model. References only `Microsoft.EntityFrameworkCore.Relational`, never Npgsql.
- `PgNotify.Migrations` — the only project referencing
  `Npgsql.EntityFrameworkCore.PostgreSQL`. Custom `IMigrationsSqlGenerator`/annotation providers
  turn the EFCore-written annotations into trigger DDL, plus the database-first
  `EnsureNotificationTriggersAsync()`/`GenerateNotificationTriggersScript()` path that bypasses
  migrations entirely.
- `PgNotify.Runtime` — the LISTEN connection, reconnect, dispatch pipeline, entity-keyed
  handlers and envelope streams, DI, health check. No EF Core dependency, so a pure listener process (no `DbContext`) is possible.
- `PgNotify.Runtime.EFCore` — the bridge: turns the models of the `DbContext`s that opted in
  into the listener's channel map, and their connection string into the listener's. References EF
  Core **Relational**, never `Npgsql.EntityFrameworkCore.PostgreSQL`, and is written entirely
  against `Runtime`'s public `INotificationMappingSource` — it needs no internals of either side,
  which is the check that the extension point is real.
- `PgNotify.Analyzers` — `netstandard2.0` (required for Roslyn analyzer hosting; the one `src/`
  project that doesn't multi-target `net10.0;net11.0`, since a Roslyn analyzer always runs hosted
  on netstandard2.0 regardless of the consuming project's `TargetFramework`). Pattern-matches on
  attribute/type names as strings — no project reference to `PgNotify.Core`.
- `benchmarks/PgNotify.Benchmarks` — BenchmarkDotNet micro-benchmarks for
  `PgNotify.Runtime`'s per-notification hot path (channel-map lookup, dispatch, payload
  deserialization, `Events<TEntity>()` fan-out, middleware pipeline overhead). Not part of
  `dotnet test`; run explicitly (see Commands above). `PgNotify.Runtime`'s `AssemblyInfo.cs`
  grants it `InternalsVisibleTo` the same way `PgNotify.Runtime.Tests` gets it, since the
  interesting hot-path types (`NotificationChannelMap`, `EntityNotificationDispatcher<TEntity>`,
  `NotificationDispatchPipeline`, `NotificationEventHub`) are `internal`.

## The parts that require reading multiple files to understand

**Fluent API and `[NotifyChanges]` converge on one write path.** Both
`NotificationOptionsBuilder<TEntity>.Save()` (`src/PgNotify.EFCore/NotificationOptionsBuilder.cs`)
and `NotifyChangesAttributeConvention` (`src/PgNotify.EFCore/Conventions/`) call the same
`NotificationConfigurationWriter.Apply(...)`, which writes a fixed set of **primitive**
(`bool`/`string`) annotations under the `Notifications:` prefix
(`NotificationAnnotationNames`). They must stay primitives: EF Core's migrations snapshot code
generator has to turn them into C# literals for `ModelSnapshot.cs`, and only primitives are
guaranteed to round-trip that pipeline without a custom `IAnnotationCodeGenerator`. The read side,
`EntityTypeNotificationExtensions.GetNotificationConfiguration()`, is the single place that
decodes those annotations back into a rich `NotificationEntityConfiguration` — the SQL generator and
the listening side's channel discovery both treat that method as the source of truth rather than
re-deriving anything from raw annotations themselves.

**`entityType.ShortName()`, never `entityType.ClrType.Name`.** When EF Core reconstructs the "old"
side of a migration diff from `ModelSnapshot.cs`, it builds entity types via the string-named
`ModelBuilder.Entity(string)` overload, which resolves `ClrType` to a `Dictionary<string, object>`
placeholder rather than the real POCO. Reading `ClrType.Name` anywhere in the notification
configuration pipeline makes the value differ silently between a freshly-built model and a
snapshot-reconstructed one — this was a real, shipped bug (see git history) and is now covered by
a regression test using `modelBuilder.SharedTypeEntity<Dictionary<string,object>>(...)` to
reproduce the scenario without needing a real `dotnet ef` round trip.

**The `Notifications:Fingerprint` annotation is a diff signal, never decoded — and it hashes the
generated SQL, not the configuration.** `NpgsqlNotificationsAnnotationProvider`
(`IRelationalAnnotationProvider`) builds the statements `NotificationTriggerSqlBuilder` would emit
for the table and hashes them, attaching the result design-time only, so EF Core's
`MigrationsModelDiffer` can detect a change even when no column/constraint changed.
`NpgsqlNotificationsMigrationsSqlGenerator` never parses this string — on a real change it
re-resolves the full `NotificationEntityConfiguration` fresh from the model and regenerates SQL
from that.

Hashing the *output* rather than describing the *input* is the whole point, because the SQL
generator skips regeneration entirely when the fingerprint is unchanged: whatever the fingerprint
cannot see is never regenerated in any deployed database. A configuration summary missed two cases,
both real bugs — adding a mapped column to an entity with an unfiltered `OnUpdate()` (the deployed
function kept watching the old column set and never fired for the new one), and any fix to the SQL
builder itself (users whose own configuration hadn't changed never received it). So there is **no**
"remember to add your new field to the fingerprint" rule to follow when extending generation:
anything that changes the generated SQL is covered by construction. The corollary is that a release
changing generated SQL produces a migration for every user at their next `migrations add` — that is
intended, and belongs in the release notes.

Line endings are *removed* before hashing (`NotificationFingerprint.StripLineEndings`), and nothing
else is: `StringBuilder.AppendLine` follows `Environment.NewLine` and raw string literals carry the
source file's own line endings, so untouched, a Windows developer and a Linux CI fingerprint the
same model differently. They are stripped rather than normalized to `\n` so the result never
depends on the repository's git configuration. Collapsing runs of spaces instead would hash
`WithChannelName("my channel")` and `WithChannelName("my  channel")` identically — exactly the
missed regeneration the fingerprint exists to prevent.

**A trigger belongs to a table, which is why sharing a table is validated rather than supported.**
All three provider entry points resolve a table's configuration through
`table.EntityTypeMappings.First()`. Two independent scenarios put more than one entity type's
configuration behind that single resolution, and `NotificationValidationConvention.RejectSharedTable`
refuses both:

- **Table-per-hierarchy inheritance.** `EntityTypeMappings.First()` is the root type, so
  notifications configured on a *derived* type were silently ignored — measured: no trigger, no
  error, nothing. Configuring the *root* instead works and has a consequence worth repeating to
  users: the trigger sees every row of the table, so a derived row notifies under the **root's**
  entity name, and an `IDatabaseNotificationHandler<Dog>` never fires. Carrying the discriminator
  in the payload would be the way to soften that, if it ever matters. Table-per-type and
  table-per-concrete-type are untouched and covered by
  `tests/PgNotify.Migrations.Tests/NotificationInheritanceTests.cs`, because there the derived
  type owns its table.
- **Table splitting** (two unrelated entity types mapped to the same table via a 1:1
  relationship) is measured to be strictly worse: which side `EntityTypeMappings.First()` resolves
  to is an EF Core implementation detail neither entity's own configuration controls, so a
  configuration on the "wrong" side alone — not just both sides at once — produced no trigger and
  no error. There is no redirect-to-the-root equivalent here (the two entities are peers, not root
  and derived), so it is always refused; see `TODO.md` for a design that would support it with one
  independent trigger per entity instead. Covered by
  `tests/PgNotify.Migrations.Tests/NotificationTableSharingTests.cs`.

The same convention also refuses notifications on an entity that isn't mapped to a table at all
(`RejectUnmappedToTable`) — most commonly a `ToView(...)` mapping with no matching `ToTable(...)`,
where `GetTableName()` is `null` and `GetNotificationConfiguration()` would otherwise silently
return `null` instead of failing at model-build time like everything else this convention checks.

**Trigger/function *removal* needs its own annotation surface, twice.** Naming-relevant state that
survives to removal time (currently just `NamePrefix`) has to be surfaced from **two** separate EF
Core provider hooks, because EF Core uses different code paths for the two removal shapes:
`NpgsqlNotificationsAnnotationProvider.For(ITable, designTime)` feeds `AlterTableOperation.OldTable`
(notifications turned off, table still exists), while
`NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove(ITable)` feeds `DropTableOperation`
(whole table removed). Missing either one is easy to do and easy to miss — there's no compile
error, just a wrong (unprefixed) name in generated `DROP TRIGGER`/`DROP FUNCTION` SQL, which is
exactly what happened the first time `NamePrefix` was added. `tests/PgNotify.Migrations.Tests/NotificationNamePrefixTests.cs`
covers both removal paths explicitly; keep both covered for any future field added to the
fingerprint that also affects naming.

**A typed event binds against the payload's JSON, not against the envelope.** `ToTyped<T>()`
deserializes `NotificationEnvelope.RawPayload` as-is, so a `record XInserted(int Id)` only binds
`Id` if the payload has a *top-level* `id` — which the minimal and projected shapes emit but the
extended one does not (it nests the key under `keys`). **Both configuration styles default to the
minimal payload**, and that is load-bearing rather than tidy: they used to disagree —
`[NotifyChanges]` minimal, `HasDatabaseNotifications()` extended — so moving an entity from one
style to the other without saying anything about the payload left every event with `Id == 0`,
silently and with no exception anywhere. The extended shape is now always asked for
(`WithPayload(NotificationPayloadKind.Extended)`), by whoever actually needs its `timestamp` or
`changed` list; `samples/HttpCaching.WebApi` is the worked example. `WithPayload(x => new { ... })`
declares the shape explicitly — and its JSON keys are the **property** names, never the column names
(`NotificationPayloadColumn` carries both), because the payload is deserialized into a .NET type
that knows nothing about storage naming. Keying on the column instead silently unbinds every
member the moment one is renamed, by `HasColumnName` or wholesale by `EFCore.NamingConventions`. `tests/PgNotify.Runtime.Tests/Serialization/PayloadShapeBindingTests.cs`
pins down all three shapes side by side (including the silent `Id == 0`), and
`tests/PgNotify.IntegrationTests/PayloadProjectionTests.cs` proves a projected payload binds
every member of its event type against a real database. The deserializer does normalize both
shapes into `Envelope.Keys`, so the key is always reachable there even when the typed projection
leaves it unbound.

**Runtime event routing has zero per-notification reflection, on both of its paths.** There are
two, and they are separate on purpose:

- **Handlers** are keyed on the *entity type*. `NotificationChannelMap` maps `(channel, entity
  name)` to a compiled `EntityNotificationDispatcher<TEntity>`, built once when
  `MapChannel<TEntity>(channel)` declares it. The operation is not in the key — the payload states
  it, and it selects which *group* of handlers runs. A handler receives the
  `NotificationEnvelope`, so this path performs no deserialization at all.
- **`Events<TEntity>()` streams** go through the same lookup and the same key: the pipeline
  publishes the envelope to `NotificationEventHub` under the entity's CLR type, and the hub only
  holds a broadcaster for a type something has actually subscribed to — so an entity nobody streams
  costs one dictionary miss.

Nothing on this path deserializes: handlers and streams both receive the `NotificationEnvelope`, and
a typed shape is produced only by an explicit `envelope.ToTyped<T>()`. A channel shared by multiple
entity types (the `SingleChannelNamingStrategy`) routes correctly because the entity name is in the
key — dropping it leaves every unit test green, so the shared-channel case is covered by integration
tests.

**Channels are declared, never inferred from handlers.** An entity type says nothing about which
channel carries it, so registering a handler class subscribes to nothing: `MapChannel<TEntity>(...)`
or `options.AddNotificationMappingFromDbContexts()` is what makes the listener `LISTEN`. A handler that never
runs is almost always a channel nobody declared.

**The mapping is resolved when the host starts, not when the services are registered.**
`PostgresNotificationHostedService.StartAsync` runs every `INotificationMappingSource`, settles the
connection string, and only then starts the listener — which reads both from `NotificationRuntimeState`
on every connection attempt. This is forced by ordering: `AddPostgresNotifications()` may legitimately
be called before the `AddDbContext` whose model the mapping comes from. Two consequences worth
knowing: a missing connection string is reported at host start rather than at registration (there is
no eager check any more, and re-adding one would break the bridge), and `NpgsqlNotificationListener`
must never capture the connection string or the channel list in its constructor.

**The `UseNpgsqlNotifications()` marker lives in `PgNotify.EFCore`, not in the package that
registers it.** `NotificationsOptionsExtension` is written by `PgNotify.Migrations` and read by
`PgNotify.Runtime.EFCore` (through the public `context.IsNotificationContext()`); putting it in
`Migrations` would make a listener reference the whole Npgsql EF Core provider to answer a yes/no
question. `Migrations` reaches it via `InternalsVisibleTo`.

**An inherited connection string is never used verbatim** — `NotificationConnectionString.ForListening`
turns `Multiplexing` and `Pooling` off, and both were measured against `postgres:16-alpine`:
`Multiplexing=true` makes `WaitAsync` throw `NotSupportedException` immediately, degrading the
listener into a permanent reconnect loop (32 reconnects in 7 s) that — measured, contrary to the
obvious assumption — still delivers every notification, so the symptom is log noise and connection
churn rather than silence. `Pooling=true` is the sharper one: pools are keyed by connection string,
so the listener's permanently-held connection takes a slot of the application's own pool
(demonstrated with `MaxPoolSize=1`, where the application then cannot open a connection at all).
That is the half `tests/PgNotify.IntegrationTests/ModelDrivenMappingTests.cs` proves by
mutation; the multiplexing half is unit-tested only, because it cannot be caught by observing
whether notifications arrive.

**Dispatch order is fixed in `NotificationDispatchPipeline`, not left to DI.** `Events<T>()`
subscribers first (an async-stream consumer must not wait behind an unrelated handler's I/O), then
the operation-specific group (`IDatabaseInsertedHandler<T>`/`Updated`/`Deleted`), then
`IDatabaseNotificationHandler<T>`, then the non-generic `IDatabaseNotificationHandler`. Only the
group matching the operation is ever resolved — that is why the operations are separate interfaces
rather than three default-implemented methods on one, and it is what makes "no handler for this
operation" cost nothing. A class implementing both an entity-keyed interface and the catch-all is
invoked twice, by design.

**The `TEntity` parameter on every handler interface is a marker: it never appears in
`HandleAsync`'s signature.** So an old event-keyed declaration like
`IDatabaseNotificationHandler<ProductUpdated>` still *compiles as a declaration* — the compiler only
objects to the unimplemented member. Nothing checks that the type argument is a mapped entity, so a
handler keyed on an event type registers happily and simply never fires.

**Change tracking never disposes the `CancellationTokenSource` it swaps out.**
`EntityChangeTracker` (`src/PgNotify.Runtime/Caching/`) replaces its CTS on every change and
deliberately leaves the old one to the GC: `CancellationTokenSource.Token` throws
`ObjectDisposedException` once disposed, and both a concurrent `GetChangeToken()` and the
`CancellationChangeToken` it hands out can still be holding that reference. Adding the "obvious"
`Dispose()` compiles and passes every single-threaded test — the guard is
`Reading_tokens_concurrently_with_changes_never_throws`, which fails within ~1s under load. The
same file's `Invalidate()` also swallows-and-logs callback exceptions on purpose: it runs both from
the dispatch pipeline (where throwing would trigger `RetryNotificationMiddleware`) and from a timer
thread (where throwing would crash the process).

**Central Package Management version pin note:** `Microsoft.CodeAnalysis.CSharp` is pinned to
`5.0.0` (not the `4.x` line) specifically because `Microsoft.EntityFrameworkCore.Design` 10.x pulls
`Microsoft.CodeAnalysis.CSharp >= 5.0.0` transitively; keep the whole `Microsoft.CodeAnalysis.*`
family (used by `PgNotify.Analyzers`) on matching major
versions or restore fails with `NU1109`/`NU1107`.

## Testing conventions

- Unit tests build real `CSharpCompilation`s / EF Core models directly rather than using
  `Microsoft.CodeAnalysis.Testing` harness packages or a database — see
  `tests/PgNotify.Analyzers.Tests/CompilationTestHelper.cs`, and
  `tests/PgNotify.Migrations.Tests/TestModels/MigrationTestHelper.cs` (drives the real
  `IMigrationsModelDiffer`/`IMigrationsSqlGenerator` pipeline and asserts on exact generated SQL
  text).
- EF Core caches the compiled model per `DbContext`-derived type by default, keyed only on the
  type — every test `DbContext` in this repo registers an `UncachedModelCacheKeyFactory`
  (`IModelCacheKeyFactory` returning a new `object()` every time) so independent tests using the
  same generic test-context type don't share a stale model.
- `PgNotify.IntegrationTests` uses `Testcontainers.PostgreSql` and actually starts hosted
  services manually (`IHostedService.StartAsync`/`StopAsync`) rather than a full generic `Host`,
  since these tests build a plain `ServiceCollection`.
