# Plan: model-driven routing, entity-scoped handlers, no code generation

Status: **all four steps implemented** on `fingerprint-from-generated-sql`. Breaking by design — the
package has no users yet, so this fixed the root cause rather than adding guards around it. The
implementation notes for each step are at the end, newest first; where the plan turned out to be
wrong, the notes say so rather than the plan being edited to match.

## Why

The listening side re-declares what EF Core's model already knows. Every `INotificationEvent`
carries `static Channel`, `static EntityName`, `static Operation`; the source generator exists to
write those three members; the `[Table("...")]` discipline exists to make the generator's guess
true; and the sample comments, `PGN003`, and two rounds of debugging in this repo's history exist
because the two sides can disagree without anything failing.

None of it is load-bearing. The channel is a property of the mapped table, which the model resolves
exactly — including `ToTable(...)`, a pluralized `DbSet`, `EFCore.NamingConventions`, the
topic/single strategies, and `WithChannelName`. A generator cannot resolve any of that at compile
time; the model resolves all of it, offline, with no database connection (verified: the migration
tests build models against an unroutable host).

So: route from the model, and delete everything that exists to approximate it.

## Decisions taken

- **Minimal payload is the default for both configuration styles.** Today `[NotifyChanges]` defaults
  to minimal and the fluent API to extended, an asymmetry that silently leaves a typed `Id` unbound
  when an entity moves between styles. Minimal everywhere: smallest payload, least row data leaving
  the database, least exposure to the 7999-byte limit, and the key at the top level where it binds.
  `HttpCaching.WebApi` must then ask for the extended payload explicitly — its ETags are built on
  the `timestamp` field only that shape carries.
- **Everything configurable fluently is configurable by attribute.** The annotation model is already
  primitive strings, so the attribute writes the same annotations through the same
  `NotificationConfigurationWriter`; nothing new has to round-trip through `ModelSnapshot.cs`.
- **Handlers are keyed on the entity, not on an event type.** Five interfaces, no generated records.
- **The channel map carries `channel ↔ entity type`, and nothing else.** No operations: the payload
  already says which one it was.
- **The map is discovered automatically** from the `DbContext`s that opted in via
  `UseNpgsqlNotifications()`. Explicit APIs remain for every case discovery cannot serve.
- **`PgNotify.MediatR` and `IExternalNotificationPublisher` are both removed.** The publisher
  abstraction exists only so `Runtime` never references MediatR; with MediatR gone, and with a
  non-generic catch-all handler receiving every notification in the same scope at the same moment,
  it is a second concept for one job.

## The listening surface

```csharp
services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs).UseNpgsqlNotifications());
services.AddPostgresNotifications();          // nothing else: channels and connection string
                                              // both come from the context that opted in
```

| case | what the user writes |
|---|---|
| single process with EF | `AddPostgresNotifications()` — nothing |
| several contexts, one wanted | `SetMappingFromContext<AppDbContext>()` |
| listener with no EF Core | `MapChannel<Product>("products")` or `MapChannel("audit_events")` |
| another service's channel, in addition | `MapChannel("legacy_audit")` — additive |

### Handler interfaces

```csharp
public interface IDatabaseNotificationHandler<TEntity>          // every operation on TEntity
{
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}

public interface IDatabaseInsertedHandler<TEntity> { Task HandleAsync(NotificationEnvelope e, CancellationToken ct); }
public interface IDatabaseUpdatedHandler<TEntity>  { Task HandleAsync(NotificationEnvelope e, CancellationToken ct); }
public interface IDatabaseDeletedHandler<TEntity>  { Task HandleAsync(NotificationEnvelope e, CancellationToken ct); }

public interface IDatabaseNotificationHandler                   // every entity, every operation
{
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
```

Separate interfaces rather than one interface with default methods, for a reason that is not
cosmetic: the container then knows who listens to what.
`GetServices<IDatabaseDeletedHandler<Product>>()` returning nothing is zero work, whereas default
methods force every handler for the entity to be resolved on every notification so that two no-ops
out of three can be invoked — on the dispatch hot path this repo already benchmarks.

The envelope carries `Operation`, so the catch-all needs no second parameter. The cache-invalidation
sample collapses to one class, one interface, one method body; the duplication it carries today
exists only because handlers are keyed on event types.

The non-generic `IDatabaseNotificationHandler` covers what `SingleChannelNamingStrategy` was built
for — one process observing all activity — and is what a polyglot or EF-free listener uses, having
no CLR entity types to key on. It is what makes `MapChannel("...")` usable rather than merely
present.

**Two rules to fix, because both are otherwise decided by DI resolution order.**

- A class implementing both the catch-all and `IDatabaseUpdatedHandler<Product>` is invoked **twice**
  on an update. That is the predictable reading — two registrations, two handlers — but it is a
  trap, so it gets documented, plus an analyzer diagnostic when a *single class* implements both for
  the same entity, which is nearly always a mistake.
- Operation-specific handlers run first, then the catch-all, in registration order within each
  group. Arbitrary, but it must be pinned down and tested.

**Naming.** Keep the `IDatabase…` prefix: `INotificationHandler<T>` is MediatR's, and even with that
package gone, users bring their own. Past tense (`Inserted`/`Updated`/`Deleted`) matches the JSON
vocabulary — a handler reacts to something already done.

### Typed payloads and streaming

No second generic parameter on handlers. A projection is converted with `e.ToTyped<OrderShape>()`,
explicitly, in the handler that configured it — a local, visible call rather than a contract that
silently binds nothing when the shapes disagree.

`Events<T>()` keeps its generic form and moves to the entity: `Events<Product>()` and
`Events<Product>(NotificationOperation.Update)`, both yielding envelopes.

## The channel map

The routing key stays `(channel, entity, operation)`, but only the first two need declaring: the
operation arrives in the payload. So the map is `channel ↔ entity type`, which covers every naming
strategy without a special case:

| strategy | map entries |
|---|---|
| per entity | `"products" → Product` |
| topic | `"product.created"`, `"product.updated"`, `"product.deleted"` → `Product` |
| single shared channel | `"app_events" → Product`, `"app_events" → Order` |

The payload's `entity` field disambiguates a shared channel; its `operation` field selects the
handler group.

### Where the map comes from

`UseNpgsqlNotifications()` feeds EF Core's *internal* service provider, so the call itself cannot
push anything into the application container. What it leaves behind is a marker:
`NotificationsOptionsExtension` on the context's options. A context carrying it is, by definition, a
notification context.

At startup the bridge enumerates the registered `DbContext` types, keeps those carrying the marker,
reads their models, and derives the map — and, holding the context, defaults the listener's
connection string to the same one. The attribute/fluent configuration already written becomes the
single source, restated nowhere.

Three practical consequences:

- **Resolution happens at startup, not at registration.** Today the channel list is captured when
  the listener is constructed; it has to move to `StartAsync`, or an
  `AddPostgresNotifications()` called before `AddDbContext` would see nothing.
- **`NotificationsOptionsExtension` is `internal sealed`.** The bridge needs access —
  `InternalsVisibleTo`, or make it public.
- **Several contexts are merged.** `SetMappingFromContext<T>()` narrows it to one.

An explicitly configured map may be checked against the deployed triggers, whose
`COMMENT ON FUNCTION` already carries a fingerprint — the database is the one place both sides
provably agree. Worth building only if hand-maintained maps turn out to be common; derived maps
cannot drift.

## What disappears

- `PgNotify.SourceGenerators` and its test project: 396 lines, a `netstandard2.0` project, an
  entry in the `PgNotify.Listener` meta-package. Its entire output for the flagship sample is
  three records and a one-constant class no sample references. Of the four members it emits, three
  are trivially derivable and the fourth — the channel — is the guess this plan removes.
- `INotificationEvent` and its three static members.
- `PgNotify.MediatR` (3 files, 48 lines, a package pin) and `IExternalNotificationPublisher`.
- The `[Table("...")]` discipline documented in two samples, and the comments explaining it.
- `{Entity}NotificationChannels`.
- `AddEvent<T>()`.

## What is untouched

The entire design-time side: `[NotifyChanges]`, the fluent API, SQL generation, the SQL-derived
fingerprint, the overflow guard, payload projection, and PGN001/002/004. `IEntityChangeTracker<T>`
needs nothing either — `EntityChangeTrackerOf<TEntity>` already resolves by `typeof(TEntity).Name`.
The most recently added feature in this repo had already keyed on the entity; this refactor brings
the rest of the runtime in line with a choice the codebase had made.

`PgNotify.Listener` becomes `Runtime` alone — **not** `Runtime + the EF bridge`. Making the
listener meta-package drag EF Core in would undo the property that justifies a separate listening
package at all.

## Attribute/fluent parity

| Fluent | Attribute |
|---|---|
| `OnInsert()` / `OnUpdate()` / `OnDelete()` | `Operations` (exists) |
| `WithNamePrefix(...)` | `NamePrefix` (exists) |
| `OnUpdate(x => new { ... })` | `WatchedProperties = [nameof(...)]` |
| `WithPayload(NotificationPayloadKind)` | `Payload = NotificationPayloadKind.Extended` |
| `WithPayload(x => new { ... })` | `PayloadProperties = [nameof(...)]` |
| `WithPayload<TBuilder>()` | `PayloadBuilder = typeof(...)` |
| `WithPerEntityChannel()` / `WithSingleChannel(n)` / `WithTopicChannel(sep)` | `ChannelStrategy` + `ChannelArgument` |
| `WithChannelStrategy(custom)` | `ChannelStrategy = typeof(...)` |
| `WithChannelName(...)` | `ChannelName` |
| `WithPayloadOverflow(...)` | `PayloadOverflow` |

Every one is a constant, a `string[]`, or a `Type`. The fluent selectors keep the advantage of being
refactor-safe expressions; the attribute forms are written with `nameof` and validated by the same
convention that already validates the fluent ones, so a typo fails at model-build time.

## Blind spots found while planning

Four issues this design has to answer for, three of which exist today. All were measured, not
assumed.

**1. TPH configuration is silently ignored — a live bug.** All three provider entry points resolve
their entity with `table.EntityTypeMappings.First()`. On a table-per-hierarchy table that first
mapping is the root, so notifications configured on a *derived* type are never seen:

```
HasDatabaseNotifications() on Dog (derived)  ->  no trigger at all, no error
HasDatabaseNotifications() on Animal (root)  ->  pg_notify('Animal', payload)
```

No test in the repo covers inheritance. The fix is a validation that refuses configuration on a
derived type and names the root instead. This refactor sharpens the consequence rather than causing
it: the trigger is per *table*, so a Dog row notifies as `entity: "Animal"`, and an
`IDatabaseNotificationHandler<Dog>` would never fire. That has to be documented, and could be
softened later by carrying the discriminator column in the payload so a handler can tell rows apart.

**2. `ShortName()` is not unique.** The payload's `entity` field is `entityType.ShortName()`, so two
`Invoice` types in different namespaces produce the same value. Distinct channels keep them apart,
but on a shared channel (`WithSingleChannel`) the `(channel, entity, operation)` routing key
collides — and automatic discovery makes that reachable without anyone choosing it. Detect the
collision while building the map, at startup, and throw. Free, and it closes the case without
touching the payload shape.

**3. Inheriting the context's connection string is not free.** Two Npgsql keywords are hostile to a
`LISTEN` connection: `Multiplexing=true` is incompatible with it outright, and `Pooling=true` means
the listener permanently holds one connection out of the application's pool. The inherited string
has to be sanitized — multiplexing off, out of the pool — not reused verbatim.

**4. Rolling deploys lose notifications across a rename.** Renaming a table renames the channel:
during a rolling deploy the old instances still `LISTEN` on the old name while the new triggers
notify the new one. Inherent to `LISTEN`/`NOTIFY` rather than caused here, but automatic discovery
hides it, so it belongs in the docs — with discovery from `COMMENT ON FUNCTION` as the mitigation
if it ever matters enough.

## Still open

- **A filter on discovery** (`except:`) — not adding it until someone asks; `MapChannel` covers the
  narrow case, `SetMappingFromContext<T>()` the multi-context one.
- **The bridge package's name** — `PgNotify.Listener.EFCore` in this document.
- **The samples, and `TaskBoard` above all.** `TaskBoard.Model` exists to share `[NotifyChanges]`
  entities *and generated events* between a writer and an EF-free watcher. With generation gone,
  only POCOs remain in it. That sample is the honest test of the EF-free story: if the watcher is
  painful to write with `MapChannel` plus the non-generic handler, that is the signal to accept EF
  Core on the listening side and simplify further.

## Order of work

The routing core keeps its `(channel, entity, operation)` key throughout — it is the one thing that
does not move.

1. The five handler interfaces and envelope-based dispatch, with the map still explicit
   (`MapChannel`). This step touches the dispatch hot path, so the existing benchmarks are the check
   that resolving two handler groups per notification costs nothing.
2. The bridge package: marker discovery, `SetMappingFromContext<T>()`, connection-string default,
   and moving channel resolution to `StartAsync`.
3. Attribute/fluent parity, the minimal default on both sides, the TPH validation, and the
   entity-name collision check.
4. Remove the source generator, MediatR, and `IExternalNotificationPublisher`; migrate the samples;
   delete the `[Table]` discipline comments.

Each step stays green. Many of the 234 existing tests will change shape rather than merely pass —
that churn is the most honest measure of how much of the current surface existed only to maintain
the redundancy this plan removes.

## Step 1 implementation notes (done)

The five interfaces, `EntityNotificationDispatcher<TEntity>`, `NotificationChannelMap`,
`MapChannel<TEntity>(...)`/`MapChannel(...)`, and the fixed dispatch order in
`NotificationDispatchPipeline`. Suite green at 258 tests (24 of them integration).

**Two things the plan assumed could be staged separately and cannot.**

- **`Events<T>()` cannot move to the entity before step 4.** `IAsyncEnumerable<T> Events<T>()` and
  `IAsyncEnumerable<NotificationEnvelope> Events<TEntity>()` differ only by return type and by a
  constraint, neither of which is part of a method signature — they cannot coexist, under any
  parameter list that also lets `Events<Product>()` compile unambiguously. So the stream API is
  untouched here and moves when `INotificationEvent` is deleted, in step 4. Consequence for step 1:
  the listener's channel list is the *union* of `MapChannel` entries and registered event types.
- **The old and new `IDatabaseNotificationHandler<T>` cannot coexist either** — same name, same
  arity. The handler surface therefore swapped in one step, and the call sites the plan scheduled
  for step 4 (`CacheInvalidation.WebApi`, `HandlerDispatchTests`, `SlowHandlerIsolationTests`) moved
  now. The sample did collapse to one class, one interface, one method body, as predicted.

**Blind spot 2 (`ShortName()` collisions) is already closed, earlier than planned.** The check
belongs wherever the map is built, and that is `NotificationChannelMap.MapChannel` — mapping two
same-named types to one channel throws there, naming both full type names. Step 3 has nothing left
to add beyond pointing the derived map at the same method.

**What the code taught that the plan did not say.**

- **The generic parameter is a marker — it never appears in `HandleAsync`'s signature.** An old
  `IDatabaseNotificationHandler<ProductUpdated>` therefore still compiles as a *declaration*; only
  the unimplemented member fails. Nothing constrains the type argument to a mapped entity, so a
  handler keyed on the wrong type registers silently and never fires. Worth a diagnostic in step 3,
  alongside the one already planned for a class implementing both a catch-all and an entity handler.
- **The benchmarks answer a bigger question than the one the plan asked.** Measured on this repo's
  BenchmarkDotNet suite, same machine, before and after (arm64, .NET 10.0.10):

  | | before | after |
  |---|---|---|
  | routing lookup | 51.2 ns / 0 B (registry) | 55.3 ns / 0 B (channel map) |
  | dispatch, no handlers | 1439 ns / 184 B | **231 ns / 0 B** |
  | dispatch, two handlers | 1585 ns / 216 B | **396 ns / 32 B** |
  | dispatch, both handler groups | — | 475 ns / 64 B |
  | whole pipeline, no middleware | 1651 ns / 216 B | 1784 ns / 216 B |

  Dispatch got ~4× cheaper and allocation-free at rest, not because routing got faster but because
  **handlers no longer deserialize anything**: the old terminal step built a typed event for every
  notification whether or not anyone wanted one. Deserialization now happens only where an
  `Events<T>()` stream or an explicit `envelope.ToTyped<T>()` asks for it — `TypedEventStreamDispatch`
  still measures 1413 ns / 184 B, and that is now an opt-in cost.

  The second handler group costs +79 ns and +32 B when it actually has a handler, and **0 B when it
  does not** — an empty `GetServices<T>()` allocates nothing on .NET 10, so the
  `IServiceProviderIsService` guard considered for the catch-all is unnecessary. It is not free in
  time, though: each empty group resolution is ~90 ns, which is what the +133 ns on the whole-pipeline
  row buys (a second lookup, two group resolutions, and the catch-all). That row also still pays the
  1.4 µs deserialization, because its setup registers an event type for the same notification — the
  honest reading is that a process consuming *only* through handlers is already ~4× cheaper per
  notification today, and one consuming streams pays what it always did until step 4.
- **Reading the key out of an envelope is the one place the new surface is clumsier than the old
  one**: `envelope.Keys["id"].GetInt32()` where a typed event gave `notification.Id`. It appears in
  the sample, in both migrated integration tests, and will appear in every handler that does
  anything. A typed accessor (`envelope.Key<int>()`, throwing a `NotificationPayloadFormatException`
  on a composite or absent key) is worth adding before the samples are rewritten in step 4.
- **`AddHandlersFromAssembly` now uses `TryAddEnumerable`.** With handlers keyed on entities rather
  than on event types, scanning two assemblies that both see a shared handler is a realistic way to
  register the same class twice — and the symptom, a handler running twice per notification, is
  invisible to any test of a single scan.

## Step 4 implementation notes (done)

The removals, `Events<T>()`'s move to the entity, and the samples. Suite green at 274 tests — 16
fewer than step 3, all of them tests of the deleted layer.

Deleted: `PgNotify.SourceGenerators` and its test project, `INotificationEvent`,
`NotificationTypeRegistry`, `TypedEventDispatcher<T>`, `PgNotify.MediatR`,
`IExternalNotificationPublisher`, `AddEvent<T>()`, `{Entity}NotificationChannels`, and the MediatR
package pin. `PgNotify.Listener` now means `Runtime + Runtime.EFCore`.

- **`Events<T>()` became `Events<TEntity>()` and `Events<TEntity>(operation)`, both yielding
  envelopes** — the move step 1 could not make while `INotificationEvent` still existed. The hub
  lost its generic parameter with it: one broadcaster per entity type, holding envelopes, created
  when something first subscribes. Publishing to an entity nobody streams is now a dictionary miss
  rather than a deserialization.
- **The two consumption styles finally share one key.** Handlers and streams are both keyed on the
  entity type and both receive the envelope, so the whole `(channel, entity, operation)` triple is
  resolved once, in one lookup, for both. Measured end to end on the same machine as step 1 — the
  whole pipeline, no middleware, one handler:

  | | before step 1 | after step 1 | after step 4 |
  |---|---|---|---|
  | pipeline, no middleware | 1651 ns / 216 B | 1784 ns / 216 B | **388 ns / 32 B** |

  Step 1 moved the deserialization off the handler path but the pipeline still paid it for the
  `Events<T>()` registry; deleting that path is what collects the 4.3× — a notification nobody
  streams is now routed, dispatched and delivered without `System.Text.Json` running at all beyond
  parsing the envelope. Dispatch itself is unchanged (241 ns / 0 B with no handlers, 348 ns / 32 B
  with two).
- **`PGN003` stays.** The plan's opening lists it among the things that exist because the two sides
  can disagree, but that is not what it does: it reports "notifications are configured and
  `AddPostgresNotifications()` was never called", which this refactor does not obsolete.
- **The `[Table("...")]` discipline is gone from `CacheInvalidation.WebApi` and stays, for a
  different reason, in `TaskBoard.Model`.** The discipline was an obligation imposed by the
  generator's guessed channel name. What remains in `TaskBoard.Model` is an ordinary mapping choice
  with a real cross-process justification: the watcher has no model to read, so the channel name is
  a contract between two processes, and pinning the table is how that contract is stated in the one
  project they share. `CacheInvalidation.WebApi` now carries no `[Table]` at all — its table is
  `Products`, whatever EF Core decides, and discovery follows.
- **The EF-free story came out better than the plan feared**, and for a reason the plan named
  without drawing the conclusion: routing keys on the entity, so what two processes share is the
  *POCO*, not a set of generated event types. `TaskBoard.Model` is now one class and one project
  reference (`Core`, for the attribute) — it no longer references `Runtime` at all — and the watcher
  is `MapChannel<TaskItem>("TaskItem")` plus three `Events<TaskItem>(operation)` loops. The
  non-generic handler was not needed for it.
- **Removing `[Table]` from the sample meant regenerating its migration**, which is where a mistake
  of mine surfaced: the edit that rewrote the entity's doc comment deleted `[NotifyChanges]` along
  with it, and `dotnet ef migrations add` produced a migration with no `Notifications:Fingerprint`
  annotation and no trigger — silently, because a model with no notification configuration is a
  perfectly valid model. Worth recording: **the repo's "probe migration must be empty" check does
  not catch this**, since the snapshot is regenerated from the same broken model and agrees with it.
  What caught it was diffing the generated file against the one it replaced. The check that would
  have caught it directly is `dotnet ef migrations script | grep pg_notify`, which is now what the
  sample was verified with.

## Step 3 implementation notes (done)

Attribute/fluent parity, the minimal payload default on both sides, the TPH validation, and the
entity-name collision check. Two commits; suite green at 290 tests.

- **The parity test is an equality between the two styles' annotation sets**, not a pair of
  behavioral assertions. That is what makes it hold in both directions — dropping any one attribute
  property from the convention fails it — and it is only possible because both styles were already
  funnelled through `NotificationConfigurationWriter`.
- **`ChannelStrategy` had to become two attribute properties**, `ChannelStrategy` (an enum) and
  `ChannelStrategyType` (a `Type`), because the plan's single `ChannelStrategy` cannot be both. Same
  for `Payload`/`PayloadBuilder`. Setting both halves is rejected rather than resolved by precedence:
  the ignored one would look applied.
- **The attribute path had never reached a real server.** Every integration test configures
  fluently, so an attribute-only entity — watched filter, topic channel, projected payload, all from
  attribute arguments — now runs end to end. It caught nothing, but the absence was real.
- **Blind spot 1 confirmed exactly as described, and it has a boundary the plan did not state.**
  Measured before writing the fix: a TPH derived type produces no trigger and no error; the root
  produces one. But table-per-type and table-per-concrete-type are *fine* — the derived type owns a
  table, so `EntityTypeMappings.First()` is that type — and a validation phrased as "no inheritance"
  would have rejected a working configuration. The rule is therefore "shares a table with a base
  type", not "has a base type".
- **The entity-name collision check needed no new code**, only a test: the derived map already goes
  through `NotificationChannelMap.MapChannel(channel, entityName, type)`, which has rejected the
  collision since step 2. Two `Invoice` types in different namespaces on one `WithSingleChannel`
  now prove it at the discovery level.

## Step 2 implementation notes (done)

`PgNotify.Runtime.EFCore`, `INotificationMappingSource`, resolution moved to `StartAsync`, and
the inherited connection string. Suite green at 275 tests (25 integration, 9 in the new bridge test
project).

**Decisions this step settled.**

- **The bridge is `PgNotify.Runtime.EFCore`, not `PgNotify.Listener.EFCore`.** The plan
  left the name open. `PgNotify.Listener` is an empty meta-package, so a package with code
  under that name would read as part of it; every other package here is named for what it contains.
- **The marker moved to `PgNotify.EFCore`** and stays `internal`, with a public
  `context.IsNotificationContext()` next to it. The plan offered `InternalsVisibleTo` or making it
  public; a third option is better than both, because it keeps an EF Core plumbing type out of the
  public API *and* keeps the Npgsql provider out of the listener's dependency graph.
  `PgNotify.Migrations`, which registers the marker, now reads it back through
  `InternalsVisibleTo`.
- **The contract between the two sides is `INotificationMappingSource`**, a public Runtime
  interface, and the bridge is written entirely against it — it needs no internals of `Runtime` and
  none of `EFCore`. That is the check that the extension point is real rather than a name for
  "whatever the bridge happens to need".
- **`AddPostgresNotifications()` no longer rejects a missing connection string eagerly.** It cannot:
  a source may supply one, and may be registered later. The check moved into `StartAsync`.

**Where the plan's surface could not be delivered as written.**

`AddPostgresNotifications()` *alone* cannot discover anything: `AddPostgresNotifications` lives in
`Runtime`, which must not reference the bridge, so referencing a package has to come with a visible
entry point. It is one extra line — `services.AddNotificationMappingFromDbContexts()` — and
`SetMappingFromContext<T>()` became `AddNotificationMappingFromDbContext<TContext>()` to match it.

**What the code taught.**

- **A built `IServiceProvider` cannot list which `DbContext` types were registered**, and the answer
  is only complete after the last `AddDbContext`. The source therefore keeps the `IServiceCollection`
  and enumerates it at `StartAsync`. Contexts registered *only* as `IDbContextFactory<T>` are
  invisible to that scan and need naming explicitly — documented rather than guessed at.
- **The map has to be keyed on the model's entity name, not on `Type.Name`.** They are the same
  until they are not (a shared-type entity), and the payload carries the model's name. Step 1's
  `MapChannel(channel, Type)` grew a `MapChannel(channel, entityName, Type)` overload for the
  derived map to use.
- **Several contexts proposing different connection strings is rejected, not arbitrated.** Taking
  the first would point the listener at a database nobody chose, and the symptom — one context's
  notifications simply never arriving — looks like anything but a connection string.
- **Blind spot 3 was half wrong, and measuring it is what showed which half.** `Pooling=true` is the
  serious one: measured with `MaxPoolSize=1`, an inherited-verbatim connection string leaves the
  application unable to open a connection at all, and the integration test fails exactly that way
  when the sanitizing is removed. `Multiplexing=true` does *not* silence the listener as assumed:
  `WaitAsync` throws `NotSupportedException` immediately, the reconnect loop spins (32 reconnects in
  7 s), and every one of 10 notifications still arrived, because each attempt's `LISTEN` round trip
  drains what is pending. It is still worth turning off — the cost is constant connection churn and
  an error per attempt — but no end-to-end test can catch it by observing whether notifications
  arrive, so it is unit-tested only. The first version of this step claimed otherwise in a doc
  comment and had an integration test that did not fail when the fix was reverted.
