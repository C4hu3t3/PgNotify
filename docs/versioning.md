# Versioning

## Package versioning

Semantic versioning across all packages, released in lockstep (a single version number for
`PgNotify.Core`, `.EFCore`, `.Migrations`, `.Runtime`, `.Runtime.EFCore`, `.Analyzers`, and
the two empty meta-packages `.Writer`/`.Listener`) — they're tightly coupled (e.g. `.Migrations`
depends on annotation names defined by `.EFCore`) and are meant to be upgraded together, the same
way `Microsoft.EntityFrameworkCore.*` packages are. `PgNotify.Core` has no dependency on EF
Core or Npgsql package versions, but
`PgNotify.EFCore`/`.Migrations` track EF Core's own major version (this repo targets EF Core
10 / Npgsql.EntityFrameworkCore.PostgreSQL 10.x on .NET 10; supporting an earlier EF Core major
would need a separate release branch, the same way Npgsql itself branches per EF Core major).

## Annotation compatibility

Every annotation this library writes is under the `Notifications:` prefix (see
`NotificationAnnotationNames`) and versioned implicitly by name, not by an explicit schema
version field. Renaming or removing an annotation key is a breaking change requiring a major
version bump, because:

- Annotation names are baked into every consumer's checked-in `ModelSnapshot.cs` and migration
  files as literal strings — an old migration file generated against `PgNotify.EFCore` 1.x
  must still produce identical annotations when re-executed against a hypothetical 2.x, or
  `dotnet ef migrations add` will report spurious pending changes (or worse, silently regenerate
  trigger SQL that wasn't actually supposed to change).
- The `Notifications:Fingerprint` string format (see `docs/architecture.md`) is an internal
  implementation detail, not a public contract — nothing should parse it. Its *presence* (as a
  migrations-diff signal) is the only thing that matters externally; its exact content may change
  between any two versions, including patch versions, as long as it stays deterministic.

Adding a **new** annotation for a new feature is not breaking (old migrations simply won't have
it, and `GetNotificationConfiguration()` treats every annotation's absence as "use the default").


## PostgreSQL version support

Targets PostgreSQL 15+ (the SQL generated — `json_build_object`, `pg_notify`, `IS DISTINCT FROM`,
`clock_timestamp()`, standard trigger syntax — has been stable across PostgreSQL major versions
for a long time; 15+ is a conservative floor chosen to match currently-supported PostgreSQL
releases, not a hard technical requirement of any specific feature used). No PostgreSQL-version
-conditional SQL generation exists today; if a future feature needs one (e.g. to use a
version-specific JSON function), it will be gated on the same `INpgsqlSingletonOptions.PostgresVersion`
Npgsql itself already uses for its own version-conditional SQL.

## .NET / EF Core support

Built and tested against .NET 10 and EF Core 10. `PgNotify.Analyzers` targets
`netstandard2.0` (required for Roslyn analyzer hosting regardless of
the consuming project's target framework) — everything else targets `net10.0` directly, using
modern C# (primary constructors, collection expressions, required members) without
multi-targeting complexity. Supporting older EF Core/.NET versions would require either
multi-targeting or separate release branches; neither is done today.
