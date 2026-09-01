namespace WelcomeEmail.Mailer;

/// <summary>
/// Bound from the <c>Smtp</c> section of <c>appsettings.json</c>. The defaults target the Mailpit
/// container in <c>../WelcomeEmail.WebApi/docker-compose.yml</c> (plaintext, no auth, port 1025).
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1025;

    public string FromAddress { get; set; } = "welcome@example.com";

    public string FromName { get; set; } = "The Example Team";
}
