using Microsoft.Extensions.Logging;
using PgNotify;
using PgNotify.Serialization;
using WelcomeEmail.Model;

namespace WelcomeEmail.Mailer;

/// <summary>
/// Sends one welcome email per new <see cref="Member"/> row. Keyed on the entity and the
/// <b>insert</b> operation specifically (<see cref="IDatabaseInsertedHandler{TEntity}"/>), so it
/// is the only handler group the pipeline resolves for a <c>Member</c> notification — and since
/// the configuration raises nothing but inserts, it is the only notification that arrives at all.
/// </summary>
/// <remarks>
/// No <c>DbContext</c>, no row re-read: <c>envelope.ToTyped&lt;NewMember&gt;()</c> binds the
/// projected payload, which already carries the address and the name the template needs.
/// <para>
/// Delivery is best-effort. A PostgreSQL <c>NOTIFY</c> is not durable — if this process is down
/// when the row is written, that notification is gone, not queued. <c>options.UseRetry()</c> in
/// <c>Program.cs</c> covers a transient SMTP failure (Mailpit restarting, a flaky relay), but a
/// production system that must never drop a welcome email would pair this with a startup sweep
/// for members with no email-sent marker — the notification is the fast path, not the only path.
/// </para>
/// </remarks>
public sealed class WelcomeEmailHandler(IWelcomeMailer mailer, ILogger<WelcomeEmailHandler> logger)
    : IDatabaseInsertedHandler<Member>
{
    public async Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var member = envelope.ToTyped<NewMember>();

        logger.LogInformation(
            "New member #{MemberId} ({Email}); sending welcome email", member.Id, member.Email);

        await mailer.SendAsync(member, cancellationToken);
    }
}
