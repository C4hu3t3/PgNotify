using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace WelcomeEmail.Mailer;

/// <summary>Composes and sends the welcome message for one new member.</summary>
public interface IWelcomeMailer
{
    Task SendAsync(NewMember member, CancellationToken cancellationToken);
}

/// <summary>
/// Sends the welcome email over SMTP with MailKit. Against the sample's Mailpit container this is
/// a plaintext, no-auth connection; <see cref="SmtpOptions"/> is where you'd point it at a real
/// relay instead.
/// </summary>
public sealed class SmtpWelcomeMailer(IOptions<SmtpOptions> options, ILogger<SmtpWelcomeMailer> logger) : IWelcomeMailer
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(NewMember member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);

        var message = new MimeMessage
        {
            Subject = $"Welcome to Example, {member.DisplayName}!",
            Body = new TextPart("plain")
            {
                Text =
                    $"""
                     Hi {member.DisplayName},

                     Thanks for signing up. Your account (member #{member.Id}) is ready to use.

                     — {_options.FromName}
                     """,
            },
        };
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(member.DisplayName, member.Email));

        using var client = new SmtpClient();

        // SecureSocketOptions.None: Mailpit speaks plaintext SMTP on 1025 with no STARTTLS. For a
        // real relay this would be SecureSocketOptions.StartTls plus client.AuthenticateAsync(...).
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.None, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        logger.LogInformation(
            "Sent welcome email to {Email} for member #{MemberId}", member.Email, member.Id);
    }
}
