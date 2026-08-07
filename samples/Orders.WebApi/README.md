# Orders.WebApi

The write side of the [`Orders` sample](../README.md#orderswebapi--ordersprojector--fully-implemented).
A minimal API that maps [`Orders.Model`](../Orders.Model)'s `Order` entity with EF Core, owns a real
`dotnet ef migrations add`-produced migration, and exposes `/api/orders`.

It never listens for its own notifications — see [`../Orders.Projector`](../Orders.Projector) for
the separate process that maintains a read-model from them.

## What it shows

- One package reference to the library, not five: `PgNotify.Writer` bundles `EFCore` + `Migrations`
  + `Analyzers` (and `EFCore` itself pulls in `Core`). Compare `Orders.WebApi.csproj` to
  `CacheInvalidation.WebApi.csproj`, which references all five individually — same runtime
  behavior, one line instead of five.
- `Migrations/*_InitialCreate.cs` — a real migration produced by
  `dotnet ef migrations add InitialCreate`, carrying the `Notifications:Fingerprint` annotation
  that `PgNotify.Migrations`' custom SQL generator (reached transitively through `PgNotify.Writer`)
  turns into the trigger/trigger function when the migration is applied.

## Running it

```bash
docker compose up -d          # starts PostgreSQL on localhost:5434 (not 5432/5433 - see docker-compose.yml)
dotnet run                    # applies the migration on startup, then serves the API
```

```bash
curl -s -X POST localhost:5199/api/orders -H 'content-type: application/json' \
  -d '{"customerName":"Alice","amount":42.50}'

curl -s localhost:5199/api/orders
```

Run [`../Orders.Projector`](../Orders.Projector) alongside it and watch `GET /summary` there update
as you create/edit/delete orders here.
