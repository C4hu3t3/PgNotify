# TaskBoard.Model

The one thing [`TaskBoard.WebApi`](../TaskBoard.WebApi) and [`TaskBoard.Watcher`](../TaskBoard.Watcher)
share: `TaskItem.cs`, a plain entity with `[NotifyChanges]` and `[Table("TaskItem")]`. Nothing
else - no EF Core, no `Npgsql.EntityFrameworkCore.PostgreSQL`, no ASP.NET Core, not even
`PgNotify.Runtime`.

`[Table("TaskItem")]` is what makes the channel name predictable for a process that cannot read the
model: the channel is the table name, and without it a `DbSet<TaskItem> Tasks` property in
`TaskBoard.WebApi` would decide it. A listener that *does* have the `DbContext` needs none of this —
see `CacheInvalidation.WebApi`.

`TaskBoard.WebApi` references this project to map `TaskItem` with EF Core; `TaskBoard.Watcher`
references it for the POCO itself, which is what its streams and handlers are keyed on
(`Events<TaskItem>()`). There is nothing else to share: routing keys on the entity type, so the
shared contract is the entity, not a set of event types that could drift from it.

This is deliberately a *smaller* dependency than referencing `TaskBoard.WebApi` directly would be:
`TaskBoard.Watcher` still ends up with zero EF Core/Npgsql package in its own dependency graph, and
still has no idea `TaskBoard.WebApi` exists - it only knows about the entity shape, the same amount
of coupling two independently-deployed services would have in a real "multiple applications, one
database" setup (see `docs/architecture.md` and `samples/README.md#multiple-applications-sharing-one-database`).
