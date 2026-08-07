# TaskBoard.WebApi

The writer half of the [`TaskBoard` sample](../README.md#taskboardwebapi--taskboardwatcher). A
plain ASP.NET Core app: maps [`TaskBoard.Model`](../TaskBoard.Model)'s `TaskItem` entity with EF
Core, a real `dotnet ef migrations add`-produced migration, a small set of `/api/tasks` endpoints,
and a static HTML page (`wwwroot/index.html`) you can use to add/rename/check off/delete tasks in
a browser.

It never listens for its own notifications - see [`../TaskBoard.Watcher`](../TaskBoard.Watcher)
for the separate process that does.

## Running it

```bash
docker compose up -d          # starts PostgreSQL on localhost:5433 (not 5432 - see docker-compose.yml)
dotnet run                    # applies the migration on startup, then serves the API + UI
```

Open the URL printed on startup (e.g. `http://localhost:5188`) in a browser and add/edit/check
off/delete a few tasks. Every change goes through `SampleDbContext.SaveChangesAsync()`, which is
what fires the `TaskItem` trigger created by the migration.

## What it shows

- `SampleDbContext.cs` maps `TaskBoard.Model.TaskItem` - the entity itself, `[NotifyChanges]` plus
  `[Table("TaskItem")]` (same pattern, and same pitfall documented in `docs/troubleshooting.md`, as
  `CacheInvalidation.WebApi`'s `Product.cs`), lives in the separate
  [`TaskBoard.Model`](../TaskBoard.Model) project so `TaskBoard.Watcher` can reference it too - see
  its README for why.
- `Migrations/*_InitialCreate.cs` - carries the `Notifications:Fingerprint` annotation; run
  `dotnet run` (or `dotnet ef database update`) to see it actually create the trigger/function.
- `wwwroot/index.html` - a real, if minimal, web interface: vanilla JS `fetch()` calls against
  `TaskEndpoints.cs`, re-rendering the list after each mutation. It does **not** use notifications
  itself - it's a normal CRUD page. The notification side of this sample is entirely
  `TaskBoard.Watcher`, running as an independent process against the same database.
