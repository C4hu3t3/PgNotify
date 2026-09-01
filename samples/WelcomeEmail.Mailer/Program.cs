using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify;
using WelcomeEmail.Mailer;
using WelcomeEmail.Model;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("WelcomeEmailDatabase")
    ?? "Host=localhost;Port=5435;Database=welcome_email_sample;Username=postgres;Password=postgres";

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddSingleton<IWelcomeMailer, SmtpWelcomeMailer>();

builder.Services.AddPostgresNotifications(options =>
{
    options.ConnectionString = connectionString;
    options.AddHandlersFromAssembly(typeof(Program).Assembly);
    options.UseLogging();

    // Retry a failed dispatch (here: an SMTP send that threw) before letting it propagate. A
    // dropped notification is still a dropped email - see WelcomeEmailHandler's remarks - but a
    // Mailpit blip or a momentarily unreachable relay is exactly what this smooths over.
    options.UseRetry();

    // This process has no DbContext to derive the mapping from, so it states it, exactly like
    // TaskBoard.Watcher: Member notifications arrive on the "Member" channel, which is the table
    // WelcomeEmail.Model pins with [Table("Member")]. The entity type named here is the same POCO
    // WelcomeEmailHandler keys on.
    options.MapChannel<Member>("Member");
});

var host = builder.Build();
await host.RunAsync();
