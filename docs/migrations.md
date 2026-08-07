# Migrations

## Enabling it

```csharp
optionsBuilder
    .UseNpgsql(connectionString)
    .UseNpgsqlNotifications();   // after UseNpgsql, not instead of it
```

This replaces three EF Core services (`IRelationalAnnotationProvider`,
`IMigrationsAnnotationProvider`, `IMigrationsSqlGenerator`) with notification-aware subclasses of
Npgsql's own implementations, and adds a convention plugin (`NotificationsConventionSetPlugin`)
that discovers `[NotifyChanges]` and validates configuration at model-build time. Do this in both
your `DbContextOptions` setup (for the running app) **and** your `IDesignTimeDbContextFactory`
(for `dotnet ef` tooling) — see `samples/CacheInvalidation.WebApi/SampleDbContextFactory.cs`.

## What gets generated, concretely

For an entity like:

```csharp
modelBuilder.Entity<Product>().HasDatabaseNotifications(o =>
{
    o.OnInsert();
    o.OnUpdate(x => x.Name);
    o.OnDelete();
});
```

mapped to table `"Product"` in the default schema, `dotnet ef migrations add` produces a
`CreateTableOperation` carrying a `Notifications:Fingerprint` annotation, and applying the
migration runs (formatted for readability — the generator emits it as one statement per line):

```sql
CREATE OR REPLACE FUNCTION "fn_Product_notify"() RETURNS trigger
    LANGUAGE plpgsql
AS $notify_trigger$
BEGIN
    IF TG_OP = 'INSERT' THEN
        PERFORM pg_notify('Product', (json_build_object(
            'entity', 'Product'::text,
            'operation', (CASE TG_OP WHEN 'INSERT' THEN 'created' WHEN 'UPDATE' THEN 'updated' ELSE 'deleted' END),
            'id', NEW."Id"
        ))::text);
    ELSIF TG_OP = 'UPDATE' THEN
        IF (NEW."Name" IS DISTINCT FROM OLD."Name") THEN
            PERFORM pg_notify('Product', (json_build_object(
                'entity', 'Product'::text,
                'operation', (CASE TG_OP WHEN 'INSERT' THEN 'created' WHEN 'UPDATE' THEN 'updated' ELSE 'deleted' END),
                'id', NEW."Id"
            ))::text);
        END IF;
    ELSIF TG_OP = 'DELETE' THEN
        PERFORM pg_notify('Product', (json_build_object(
            'entity', 'Product'::text,
            'operation', (CASE TG_OP WHEN 'INSERT' THEN 'created' WHEN 'UPDATE' THEN 'updated' ELSE 'deleted' END),
            'id', OLD."Id"
        ))::text);
    END IF;
    RETURN NULL;
END;
$notify_trigger$;

DROP TRIGGER IF EXISTS "trg_Product_notify" ON "Product";

CREATE TRIGGER "trg_Product_notify"
    AFTER INSERT OR UPDATE OR DELETE ON "Product"
    FOR EACH ROW
    EXECUTE FUNCTION "fn_Product_notify"();
```

(This example uses the minimal payload for brevity; the extended payload's `json_build_object`
call additionally includes `schema`, `table`, `keys` (a nested `json_build_object` of every
primary key column, used instead of a bare `id` whenever the key is composite), `changed` (a
`text[]` built from a `VALUES (...) WHERE t.changed` expression comparing `NEW`/`OLD` per watched
column — empty for insert/delete), and `timestamp` (`clock_timestamp()`).)

## Idempotency and "ALTER"

PostgreSQL has no `ALTER TRIGGER ... AS` — a trigger's definition can't be edited in place, and
neither can a function body be "diffed." So every regeneration follows the same three-statement
shape: `CREATE OR REPLACE FUNCTION` (always safe to re-run), `DROP TRIGGER IF EXISTS` (a no-op if
nothing existed), `CREATE TRIGGER` (now safe, since the trigger was just dropped or never
existed). This makes the whole thing idempotent: running the same migration's SQL twice, or
generating a migration against an unchanged model, produces identical results — verified directly
in `PgNotify.Migrations.Tests` (diffing a model against itself yields zero operations) and
via `dotnet ef migrations add` against the sample project (an empty migration).

Only the `AlterTableOperation` path actually compares old vs. new: if an entity's table is
altered for an unrelated reason (a new unrelated column, a comment change, ...) and the
notification fingerprint didn't change, no trigger SQL is emitted at all for that migration.

## Trigger and function naming

Deterministic, from schema/table/prefix only (not from any other configuration):
`{NamePrefix}trg_{table}_notify` and `{NamePrefix}fn_{schema_}{table}_notify` (schema omitted
when using the default schema; `NamePrefix` empty by default), each truncated and hash-suffixed
if it would exceed PostgreSQL's 63-byte identifier limit (see `PostgresIdentifier.EnsureWithinLength`).
Because naming depends only on these, `DROP TRIGGER`/`DROP FUNCTION` on table removal never needs
the "old" entity's full configuration — only whether notifications were enabled, the schema/table,
and the prefix that was in effect.

### Avoiding name collisions with a custom prefix

`CREATE OR REPLACE FUNCTION`/`CREATE TRIGGER` overwrite anything with the same name — if your
database already has a function or trigger that happens to match the default
`fn_{table}_notify`/`trg_{table}_notify` pattern (common in a database-first project, or one
sharing a database with other tools), this library would silently take it over. Set a prefix so
generated objects are unambiguous:

```csharp
// Per entity:
modelBuilder.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_"));
// ...or via the attribute:
[NotifyChanges(NamePrefix = "myapp_")]
public class Product { ... }

// Or once, for every entity in the model that doesn't set its own:
modelBuilder.HasNotificationNamePrefix("myapp_");
```

Precedence: an entity's own `WithNamePrefix`/`NamePrefix` wins over the model-wide default, which
wins over the empty (original) default. With `"myapp_"`, `Product` gets
`myapp_fn_Product_notify` / `myapp_trg_Product_notify` instead of `fn_Product_notify` /
`trg_Product_notify`. This applies identically whether triggers are generated via migrations or
via `EnsureNotificationTriggersAsync()`/`GenerateNotificationTriggersScript()` (below) — the
prefix is part of the entity's notification configuration either way.

**Changing the prefix on an already-configured entity is handled**: the migration that picks up
the new prefix first drops the old-prefixed trigger/function (using the *old* prefix, read off the
`AlterTableOperation`'s `OldTable`) before creating the new-prefixed pair — the same
drop-old/create-new sequence a table rename gets (see [Known limitations](#known-limitations)
below for the one case this doesn't cover: changing the prefix in the same migration as a rename).

## Composite keys

`MinimalNotificationPayloadBuilder`'s `id` field only makes sense for a single-column key; for a
composite key it automatically falls back to the same `keys` object the extended payload always
uses:

```json
{"entity": "OrderLine", "operation": "updated", "keys": {"OrderId": 42, "LineNumber": 3}}
```

## Schema-qualified tables

`b.ToTable("Invoice", "billing")` produces schema-qualified DDL throughout
(`billing."Invoice"`, `billing."fn_billing_Invoice_notify"`, ...), using Npgsql's own identifier
delimiter (which only quotes parts that actually need it — a lowercase, simple schema name like
`billing` is emitted unquoted).

## Removing notifications

Turning `HasDatabaseNotifications()` off (or removing the entity's `[NotifyChanges]`) for an
existing entity produces an `AlterTableOperation` whose migration drops the trigger and function
(`DROP TRIGGER IF EXISTS ...; DROP FUNCTION IF EXISTS ... CASCADE;`) and emits no `CREATE`
statements. Dropping the table entirely does the same cleanup — trigger first (it needs the table
to still exist), then the table itself, then the function (independent of table existence, but
grouped with the trigger cleanup for a single clear block in the generated SQL).

## Database-first (without `dotnet ef migrations`)

Everything above assumes EF Core owns your schema history. Plenty of real projects don't work
that way — the tables already exist, or are owned by Flyway/DbUp/hand-written SQL, and EF Core is
only ever used to *read/write* them, never to create them. `PgNotify.Migrations` supports
that directly, via three extension methods on `context.Database` that bypass
`IMigrationsModelDiffer` entirely and talk straight to the compiled EF Core model:

```csharp
// At startup, apply/refresh the trigger DDL for every HasDatabaseNotifications()-configured
// entity directly (tables must already exist):
await context.Database.EnsureNotificationTriggersAsync();

// ...or, if you'd rather check the DDL into Flyway/DbUp/a versioned SQL folder instead of having
// the library apply it at runtime, get the same SQL as a script:
var sql = context.Database.GenerateNotificationTriggersScript();
```

Both produce exactly the same idempotent `CREATE OR REPLACE FUNCTION` / `DROP TRIGGER IF EXISTS` /
`CREATE TRIGGER` statements the migrations path does (see above) — `EnsureNotificationTriggersAsync`
just executes them immediately via `ExecuteSqlRawAsync` instead of packaging them into a
`MigrationOperation`. `UseNpgsqlNotifications()` is still worth chaining onto `UseNpgsql(...)` even
if you never run `dotnet ef migrations add` — it's what wires up `[NotifyChanges]` attribute
discovery and `NotificationValidationConvention`'s model-build-time checks; only the *migrations*
half of what it registers goes unused.

**Skipping entities that are already up to date**: unlike the migrations path, there's no EF Core
differ to ask "did anything change" — so `EnsureNotificationTriggers`/`EnsureNotificationTriggersAsync`
do their own, cheaper version of the same check. Every deployed function gets a
`COMMENT ON FUNCTION ... IS '<hash>'` recording a SHA-256 hash of the same deterministic fingerprint
the migrations path computes for its `Notifications:Fingerprint` annotation (see
`NotificationFingerprint.ComputeHash`) — a fixed-length hex digest, not the fingerprint text itself,
so the comment isn't meant to be read as configuration (a hash is one-way; there's nothing to
decode from it, only to compare). Each call reads every configured entity's current comment (and
whether its trigger still exists) in a single query, and only runs
`CREATE OR REPLACE FUNCTION`/`DROP TRIGGER`/`CREATE TRIGGER` for entities where the hash changed or
the trigger is missing — an unconditional call with nothing to do issues one read query and no DDL
at all. If a trigger is dropped manually (or by something else) while its function's comment is
left untouched, that still counts as "not deployed" and gets recreated — the check requires both to
match, not just the comment. The very first call after upgrading from a version of this library
that didn't set this comment behaves exactly like today (no comment to compare against, so
everything is (re)applied once, and the comment starts being recorded from then on).
`GenerateNotificationTriggersScript()` does not do this check — it's meant to be a deterministic
script generated from the model alone, so it always renders every configured entity's DDL
(including the `COMMENT ON FUNCTION` statement, so a database seeded via that script is immediately
recognized as up to date if `EnsureNotificationTriggersAsync` is ever run against it afterward).

This fingerprint also reacts to more than just `[NotifyChanges]`/fluent configuration: for an
entity with an unfiltered `OnUpdate()` (no explicit column selector, so the generated trigger
watches every mapped column), the hash is computed from the table's *actual current* column list,
not just the configuration. Adding a column to that table externally (via Flyway/DbUp/raw SQL,
without touching the entity's notification configuration at all) changes the hash, so the next
`EnsureNotificationTriggersAsync()` call regenerates the function to also guard/report on the new
column — it isn't silently skipped just because nothing in the C# configuration changed.

**The remaining tradeoff versus the migrations path**: it still doesn't drop triggers for entities
that used to be configured but no longer are — clean those up the same way you'd clean up any
other schema element your database-first workflow retired (`DROP TRIGGER`/`DROP FUNCTION`, or just
leave the harmless orphan). Verified end-to-end (a table created with plain SQL, no EF migration
involved at any point) in `PgNotify.IntegrationTests/DatabaseFirstTests.cs`.

## Known limitations

- Renaming a table (`RenameTableOperation`) and changing an entity's `NamePrefix` are both handled:
  each drops the old-named trigger/function (using the *old* table name / prefix) before creating
  the new-named pair, so nothing is orphaned. The one combination that isn't covered is doing
  *both* in the same migration — renaming a table *and* changing its `NamePrefix` (or turning
  notifications off) at once. `Generate(RenameTableOperation, ...)` only ever resolves the
  *current* configuration under the *new* table name, so it drops the old-named objects using
  whatever prefix is in effect *now*, not whatever was in effect before the rename. Split such a
  change across two migrations, or drop the orphaned old-named objects manually
  (`DROP TRIGGER`/`DROP FUNCTION`) if you hit this.
- `WHERE` clause filtering at the `CREATE TRIGGER` level (PostgreSQL's `WHEN (...)` clause) is
  intentionally not used — the watched-column guard lives inside the function body instead, so one
  function can serve `INSERT`/`UPDATE`/`DELETE` uniformly. This has no behavioral downside, just a
  minor performance note (see `docs/performance.md`).
