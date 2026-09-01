# WelcomeEmail.Mailer

The listen side of the [`WelcomeEmail` sample](../README.md#welcomeemailwebapi--welcomeemailmailer--fully-implemented):
a console app that sends exactly one welcome email per new `Member` row, over SMTP, and does
nothing else.

## What it shows

- **A process with zero database access.** It references [`WelcomeEmail.Model`](../WelcomeEmail.Model)
  for the `Member` POCO and `PgNotify.Runtime` — no EF Core, no
  `Npgsql.EntityFrameworkCore.PostgreSQL`, no `DbContext`. `WelcomeEmailHandler` reads
  `envelope.ToTyped<NewMember>()`, and the projected payload
  (`WithPayload(m => new { m.Email, m.DisplayName })`, configured on the write side) already
  carries the address and the name. Contrast [`Orders.Projector`](../Orders.Projector), which keeps
  its own `DbContext` precisely because its minimal payload forces a re-read.
- **`NewMember.cs`** — the typed payload shape. Its properties are the *entity property* names
  (`Email`, `DisplayName`), never column names: a projected payload is deserialized into a .NET
  type that knows nothing about storage naming. `Id` binds because the projected shape (like the
  minimal one) puts the key at the payload's top level.
- **`WelcomeEmailHandler.cs`** — `IDatabaseInsertedHandler<Member>`, so the pipeline resolves only
  the insert group; and since the configuration raises nothing but inserts, that is the only
  notification that ever arrives.
- **`Program.cs`** — a plain `Host.CreateApplicationBuilder` console host. No `DbContext` to derive
  the mapping from, so it names the channel itself: `MapChannel<Member>("Member")`, matching the
  `[Table("Member")]` on the shared model. It also adds `options.UseRetry()`.
- **`SmtpWelcomeMailer.cs`** — MailKit against the sample's Mailpit container (plaintext, no auth on
  port 1025). `SmtpOptions` is where you'd point it at a real relay.

## Delivery is best-effort

A PostgreSQL `NOTIFY` is **not durable**. If this process is down when a member row is written,
that notification is gone — not queued, not replayed on reconnect. `options.UseRetry()` covers a
transient SMTP failure (Mailpit restarting, a flaky relay); it does nothing for a notification that
was never received. A system that must never drop a welcome email would pair this with a startup
sweep for members that have no "email sent" marker yet — the notification is the fast path, not the
only path.

## Running it

Needs [`../WelcomeEmail.WebApi`](../WelcomeEmail.WebApi) running first (it owns the migration that
creates the `Member` table and trigger) and its `docker compose up -d` (PostgreSQL on 5435,
Mailpit on 1025/8025).

```bash
dotnet run
```

Sign someone up through `WelcomeEmail.WebApi`'s API and watch:

```
info: WelcomeEmail.Mailer.WelcomeEmailHandler[0]
      New member #1 (alice@example.com); sending welcome email
info: WelcomeEmail.Mailer.SmtpWelcomeMailer[0]
      Sent welcome email to alice@example.com for member #1
```

Then open <http://localhost:8025> to read the message Mailpit captured.
