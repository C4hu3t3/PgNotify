# TaskBoard.Watcher

The listener half of the [`TaskBoard` sample](../README.md#taskboardwebapi--taskboardwatcher). A
console app with **no EF Core dependency at all** - no `Microsoft.EntityFrameworkCore` package, no
`Npgsql.EntityFrameworkCore.PostgreSQL` package, no project reference to `TaskBoard.WebApi` -
proving `PgNotify.Runtime` really doesn't need a `DbContext` to work. It does reference
[`TaskBoard.Model`](../TaskBoard.Model), the small shared project that just declares the
`TaskItem` entity - see that project's README for why sharing only that (and not EF Core itself)
is the point.

## Running it

Start [`TaskBoard.WebApi`](../TaskBoard.WebApi) first (it owns the migration that creates the
`TaskItem` table/trigger), then in a separate terminal:

```bash
dotnet run
```

Change data through `TaskBoard.WebApi`'s web page and watch lines like this appear:

```
Listening for TaskItem notifications. Press Ctrl+C to exit.
[14:32:07] + created #1
[14:32:11] ~ updated #1
[14:32:15] - deleted #1
```

## What it shows

- The `TaskItem` POCO from `TaskBoard.Model`, and nothing else: routing keys on the entity type, so
  this process subscribes with `Events<TaskItem>()` and no event type exists anywhere. What's
  actually shared between this process and `TaskBoard.WebApi` is exactly one small project with no
  EF Core reference of its own - see
  `docs/architecture.md`/`samples/README.md#multiple-applications-sharing-one-database` for the
  general pattern this implements.
- `NotificationPrinter.cs` - a `BackgroundService` running three concurrent
  `IPostgresNotificationService.Events<TaskItem>(operation)` loops, one per operation, each just
  formatting and printing what it receives.
- `Program.cs` - a plain `Host.CreateApplicationBuilder` console host. With no `DbContext` to derive
  the mapping from, it names the channel itself: one `MapChannel<TaskItem>("TaskItem")` line, which
  is the whole cost of the EF-free listening story.
