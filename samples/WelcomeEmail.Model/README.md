# WelcomeEmail.Model

The one thing [`WelcomeEmail.WebApi`](../WelcomeEmail.WebApi) (which maps this with EF Core and
owns the migration) and [`WelcomeEmail.Mailer`](../WelcomeEmail.Mailer) (which never touches EF
Core at all) share: `Member.cs`, a plain entity with `[Table("Member")]`. Nothing else — no EF
Core, no `Npgsql.EntityFrameworkCore.PostgreSQL`, no ASP.NET Core, not even `PgNotify`.

## Why no `[NotifyChanges]` here

Unlike [`TaskBoard.Model`](../TaskBoard.Model) and [`Orders.Model`](../Orders.Model), this entity
carries **no notification attribute**. This sample asks for a *projected* payload
(`WithPayload(m => new { m.Email, m.DisplayName })`), and only the fluent API can declare a payload
shape — so the configuration lives in `WelcomeEmail.WebApi`'s `SampleDbContext.OnModelCreating`
instead. That is not a limitation being worked around; it's the point of the sample: the projected
payload is what lets `WelcomeEmail.Mailer` be a process with **zero database access**, rendering
the whole email from the notification itself.

## Why `[Table("Member")]`

Same cross-process reason as `TaskBoard.Model`: the mailer has no model to read, so it names its
own channel (`MapChannel<Member>("Member")`), and the per-entity channel is the mapped table name.
Pinning the table here keeps that name from being decided by a `DbSet<Member>` property the mailer
never sees. A process that *does* have the `DbContext` needs none of this — see
[`CacheInvalidation.WebApi`](../CacheInvalidation.WebApi).

See [`samples/README.md`](../README.md#welcomeemailwebapi--welcomeemailmailer--fully-implemented)
for the full picture.
