# TODO

Tracks the remaining remediation work from the multi-agent audit. Complements `CLAUDE.md` (which
documents the *why* of choices already made) rather than duplicating it.

## Table splitting: support for two independent triggers

Currently (batch 2), `NotificationValidationConvention.RejectSharedTable` refuses any notification
configuration on an entity that shares its table with another entity outside its own hierarchy
(table splitting, e.g. `OrderHeader`/`OrderDetail` in a 1:1 relationship on the same table) — see
the `"table splitting"` error message in
`src/PgNotify.EFCore/Conventions/NotificationValidationConvention.cs`.

That rejection is correct but restrictive: PostgreSQL allows several independent `AFTER UPDATE`
triggers on the same table for the same event — they all fire. So it would be possible to generate
an independent trigger per notified entity instead of rejecting the configuration. That requires:

- Generalizing the 4 call sites (`NpgsqlNotificationsAnnotationProvider.For`,
  `NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove`,
  `NpgsqlNotificationsMigrationsSqlGenerator`, `DatabaseFacadeNotificationsExtensions.PrepareWork`)
  from "one configuration per table" to "N configurations per table, one per notified entity" —
  `PrepareWork` currently explicitly deduplicates by table (`processedTables.Add`), which would need
  to be dropped for this case.
- A multi-valued fingerprint per table (a `Notifications:Fingerprint:<EntityName>` annotation per
  notified entity instead of a single one per table); the differ in
  `NpgsqlNotificationsMigrationsSqlGenerator.Generate(AlterTableOperation...)` would need to compare
  a set, not a single value.
- Disambiguating trigger/function naming (`NotificationTriggerSqlBuilder.GetNames`) as soon as a
  table is structurally shared by several entities, even if only one currently has notifications
  enabled — to avoid enabling notifications on the second one renaming the first one's trigger.
- Fixing a latent bug along the way: an unfiltered `OnUpdate()` currently uses `allTableColumns` —
  every physical column of the table; under table splitting it needs to be restricted to the
  columns actually mapped by the entity itself (`entityType.GetProperties()`), otherwise
  `OrderHeader` would fire on a change to `ShippingAddress`, which belongs to `OrderDetail`.
- The TPH case stays unchanged (a single trigger on the root) — this rework only applies to table
  splitting outside a hierarchy.

To be handled as its own standalone batch; not started yet.

## Remaining batches from the initial audit (not yet detailed)

Investigation not done yet for these two batches — to be scoped before implementation, like every
batch before them:

- **Batch 5** — packaging / analyzer.
- **Batch 6** — documentation fixes (`docs/`, `CLAUDE.md`).
