# HTTP Caching Sample (`IChangeToken` / ETag)

Demonstrates `options.AddChangeTracking()`: PostgreSQL tells the app when a table changed, and the
app turns that into an `IMemoryCache` expiration token and an HTTP `ETag`/`304 Not Modified`.

There is **no `IDatabaseNotificationHandler<T>` anywhere in this sample** — that's the point. No
handler evicts anything by hand; the cache entry simply lives until the database says the table
changed. Compare with [`../CacheInvalidation.WebApi`](../CacheInvalidation.WebApi), which does the
same job with an explicit handler calling `cache.Remove(...)`.

## What it shows

- `SampleDbContext.cs` — `HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Extended))`.
  The extended payload is asked for explicitly (minimal is the default either style) because it is
  the only shape carrying `timestamp`, the trigger's own clock, identical on every instance.
- Nothing declares an event type or a channel name: `options.AddNotificationMappingFromDbContexts()` derives
  both from the model, and change tracking consumes the raw envelope as middleware.
- `EntityETagFilter.cs` — an `IEndpointFilter` that builds the `ETag` from
  `tracker.LastModified` + a stable hash of the query string and returns `304` when it matches
  `If-None-Match`. The MVC equivalent is the same code in an `ActionFilterAttribute`.
- `ArticleEndpoints.cs` — `GET /articles` caches through `entry.AddExpirationToken(tracker.GetChangeToken())`;
  `POST`/`PUT`/`DELETE` mutate through EF Core and invalidate nothing themselves.
- `Program.cs` — `AddChangeTracking(TimeSpan.FromMilliseconds(200))`, plus `EnsureCreatedAsync()`
  and `EnsureNotificationTriggersAsync()` at startup instead of `dotnet ef migrations`.

## Running it

No `dotnet ef` needed either way — startup creates the database and the `Article` table if they
don't exist, then applies the trigger and trigger function.

### With the bundled container

```bash
docker compose up -d          # PostgreSQL on localhost:5433
dotnet run
```

The container publishes **5433**, not the default 5432, so it can run alongside
`CacheInvalidation.WebApi`'s container and any PostgreSQL you already have locally.
`appsettings.Development.json` already points the sample at that port, so `dotnet run` needs no
further configuration. Data is not persisted to a volume — `docker compose down` throws it away.

### Against a PostgreSQL you already have

Docker is a convenience here, not a requirement: any reachable PostgreSQL works. The
non-Development default is `Host=localhost;Database=http_caching_sample;Username=postgres;Password=postgres`;
override it with an environment variable:

```bash
export ConnectionStrings__SampleDatabase="Host=localhost;Database=http_caching_sample;Username=me;Password=secret"
dotnet run
```

The user needs `CREATEDB` the first time, and `CREATE FUNCTION`/`CREATE TRIGGER` on the target
database; to use an existing database instead, name it in the connection string —
`EnsureCreatedAsync()` leaves an existing schema alone.

Then, in another terminal (the app listens on `http://localhost:5091`):

```bash
curl -s -X POST localhost:5091/articles -H 'content-type: application/json' \
  -d '{"title":"Hello","tag":"news"}'

# First GET: cache miss (see the app log), and note the ETag
etag=$(curl -si localhost:5091/articles | grep -i '^etag:' | tr -d '\r' | cut -d' ' -f2)

# Same request with the ETag: 304, no body, no query - and no cache miss in the log
curl -si localhost:5091/articles -H "If-None-Match: $etag" | head -1

# Change the row from *outside* the app entirely, to prove nothing in the request path
# is doing the invalidating:
docker compose exec -T postgres \
  psql -U postgres -d http_caching_sample -c "UPDATE \"Article\" SET \"Title\" = 'Hello v2'"

# Same ETag now returns 200 with the new content (and a fresh cache miss in the log)
curl -si localhost:5091/articles -H "If-None-Match: $etag" | head -1
```

`GET /articles/state` shows the tracker's `LastModified`; `GET /health` reports the listener's
connection state.

## Things worth noticing

- **The ETag is table-wide.** It's derived from one `LastModified` per entity, so any change to any
  article invalidates every article URL. That's the right trade for list endpoints and small
  tables; for per-row validators you'd want a per-row version column instead — the notification
  payload's `keys` field is what a per-row tracker would key on.
- **`Cache-Control: public, no-cache`** means "cache it, but revalidate every time" — which is what
  makes the `304` path the common case. It does *not* mean "don't cache".
- **No `Vary: If-None-Match`.** `Vary` lists request headers that change the *content* of a
  response; conditional-request headers aren't among them, and advertising it there only fragments
  downstream caches.
- **The coalescing window is a latency/stampede trade.** With
  `AddChangeTracking(TimeSpan.FromMilliseconds(200))`, a 500-row `UPDATE` produces two
  invalidations rather than 500. The first is immediate, so no reader is ever served stale data
  for the window; only the redundant ones are collapsed. Easy to see with the container running:

  ```bash
  docker compose exec -T postgres \
    psql -U postgres -d http_caching_sample -c \
    "INSERT INTO \"Article\" (\"Title\",\"Tag\") SELECT 'bulk-'||g, 'news' FROM generate_series(1,200) g;
     UPDATE \"Article\" SET \"Tag\" = 'updated';"
  ```

  The log shows one `Received notification` line per row (the trigger is `FOR EACH ROW`), but the
  next `GET /articles` is a single cache miss.
