# Cache Invalidation Sample

Demonstrates the full path end to end: `[NotifyChanges]` attribute → `dotnet ef migrations add` →
runtime listener → `IDatabaseNotificationHandler<Product>` → `IMemoryCache` eviction.

## What it shows

- `Product.cs` — a plain entity with `[NotifyChanges]` and no other configuration. This alone
  gets it insert/update/delete notifications with the minimal payload.
- `Migrations/*_InitialCreate.cs` — a real migration produced by
  `dotnet ef migrations add InitialCreate`, carrying the `Notifications:Fingerprint` annotation
  that `PgNotify.Migrations`' custom SQL generator turns into the trigger/trigger function
  when the migration is applied.
- `ProductCacheInvalidationHandler.cs` — an `IDatabaseNotificationHandler<Product>` that evicts
  the corresponding `IMemoryCache` entry on any change to the row. It is keyed on the entity, not
  on an event type, so one method body covers insert, update and delete — and nothing names a
  channel anywhere: `options.AddNotificationMappingFromDbContexts()` in `Program.cs` derives it (and the
  listener's connection string) from `SampleDbContext`'s model. It reacts to *any* change to the row, including ones made by another process or a raw
  `psql` session — not just changes made through this API instance's own `SaveChangesAsync()`.
- `ProductEndpoints.cs` — a cache-aside `GET /products/{id}`, plus `POST`/`PUT`/`DELETE` that
  mutate through EF Core and rely entirely on the notification handler for cache invalidation
  (no manual `cache.Remove(...)` calls in the request handlers).

## Running it

```bash
docker compose up -d          # starts PostgreSQL on localhost:5432
dotnet run                    # applies the migration on startup, then serves the API
```

Then, in another terminal:

```bash
# Create a product
curl -s -X POST localhost:5000/products -H 'content-type: application/json' \
  -d '{"name":"Widget","price":9.99}' | tee /tmp/product.json

id=$(jq .id /tmp/product.json)

# First GET is a cache miss (logged); second is a cache hit
curl -s localhost:5000/products/$id
curl -s localhost:5000/products/$id

# Update it — watch the app's log: the notification handler fires and evicts the cache entry
curl -s -X PUT localhost:5000/products/$id -H 'content-type: application/json' \
  -d '{"name":"Widget v2","price":12.99}'

# Next GET is a cache miss again, returning the updated row
curl -s localhost:5000/products/$id
```

`GET /health` reports the notification listener's connection state (via
`PgNotify.Runtime`'s built-in health check).
