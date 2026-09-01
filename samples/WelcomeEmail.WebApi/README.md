# WelcomeEmail.WebApi

The write side of the [`WelcomeEmail` sample](../README.md#welcomeemailwebapi--welcomeemailmailer--fully-implemented).
A minimal API where `POST /api/members` (a sign-up) writes one row and returns — it does nothing
about email at all. The welcome email is [`../WelcomeEmail.Mailer`](../WelcomeEmail.Mailer)'s job,
driven by the database's own `INSERT` notification.

That split is the whole point: the request path carries no SMTP latency and no SMTP failure mode,
and it behaves identically with one API instance or ten — none of them coordinate on who sends the
email, because none of them send it.

## What it shows

- `SampleDbContext.cs` — `HasDatabaseNotifications(o => o.OnInsert().WithPayload(m => new { m.Email, m.DisplayName }))`.
  `OnInsert()` only: editing or deleting a member puts nothing on the wire. The projected payload
  emits `Email` and `DisplayName` straight off `NEW` in the trigger, so the notification carries
  everything the email template needs and the mailer never has to open the database.
- `Migrations/*_InitialCreate.cs` — a real migration from `dotnet ef migrations add InitialCreate`,
  carrying the `Notifications:Fingerprint` annotation that `PgNotify.Migrations` (reached
  transitively through `PgNotify.Writer`) turns into the `INSERT` trigger and trigger function.
- One package reference to the library — `PgNotify.Writer` bundles `EFCore` + `Migrations` +
  `Analyzers` — exactly like [`Orders.WebApi`](../Orders.WebApi).

## Running it

```bash
docker compose up -d          # PostgreSQL on localhost:5435, Mailpit on 8025 (UI) / 1025 (SMTP)
dotnet run                    # applies the migration on startup, then serves the API
```

The PostgreSQL container publishes **5435**, not the default 5432, so it runs alongside every other
sample's container (`CacheInvalidation.WebApi` 5432, `HttpCaching.WebApi`/`TaskBoard.WebApi` 5433,
`Orders.WebApi` 5434).

Start [`../WelcomeEmail.Mailer`](../WelcomeEmail.Mailer) in another terminal, then sign someone up:

```bash
curl -s -X POST localhost:5202/api/members -H 'content-type: application/json' \
  -d '{"email":"alice@example.com","displayName":"Alice"}'

curl -s localhost:5202/api/members
```

Open <http://localhost:8025> and watch the welcome email arrive. It works the same if you insert a
row with raw `psql` instead of the API — the trigger fires on the write, not on the request.
