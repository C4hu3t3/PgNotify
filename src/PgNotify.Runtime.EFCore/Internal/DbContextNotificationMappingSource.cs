using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PgNotify.Internal;

/// <summary>
/// Derives the listener's channels from the model of every registered <c>DbContext</c> that opted
/// into notifications, and offers that context's connection - string and, when needed, a
/// credential-carrying template - as the listener's; see
/// <see cref="NotificationMappingBuilder.UseConnection"/>.
/// </summary>
/// <param name="services">
/// The application's service collection, kept to enumerate which <c>DbContext</c> types were
/// registered. A built <see cref="IServiceProvider"/> cannot answer that question — it resolves
/// types it is asked for, it does not list them — and the answer is only complete once every
/// <c>AddDbContext</c> call has run, which is why this is read when the host starts rather than
/// when this source is registered.
/// </param>
/// <param name="contextType">
/// The single context to read, or <see langword="null"/> to read every registered one.
/// </param>
/// <param name="scopeFactory">Creates the scope each <c>DbContext</c> is resolved in.</param>
/// <param name="logger">Reports how many channels each context contributed.</param>
internal sealed class DbContextNotificationMappingSource(
    IServiceCollection services,
    Type? contextType,
    IServiceScopeFactory scopeFactory,
    ILogger<DbContextNotificationMappingSource> logger) : INotificationMappingSource
{
    public Task ContributeAsync(NotificationMappingBuilder builder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Its own scope, not the caller's: a DbContext is scoped, and this source is a singleton.
        using var scope = scopeFactory.CreateScope();

        foreach (var candidate in ContextTypes())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scope.ServiceProvider.GetService(candidate) is not DbContext context)
            {
                continue;
            }

            if (!context.IsNotificationContext())
            {
                // Silence is right for discovery — a context that never called
                // UseNpgsqlNotifications() has nothing to say — but wrong when the caller named
                // this one: they asked for its channels and would get none, with no error.
                if (contextType is not null)
                {
                    throw new InvalidOperationException(
                        $"'{candidate.Name}' was named as the source of the notification mapping, but it did not opt into " +
                        "notifications: chain UseNpgsqlNotifications() onto its UseNpgsql(...) call.");
                }

                continue;
            }

            Contribute(builder, context, candidate);
        }

        return Task.CompletedTask;
    }

    private void Contribute(NotificationMappingBuilder builder, DbContext context, Type candidate)
    {
        var channelCount = 0;
        foreach (var binding in context.Model.GetNotificationChannels())
        {
            builder.MapChannel(binding.Channel, binding.EntityName, binding.ClrType);
            channelCount++;
        }

        // Never verbatim, and not necessarily a password either: the application's connection is
        // written for pooled, possibly multiplexed query connections - and, when configured via
        // UseNpgsql(NpgsqlDataSource, ...), its own connection string omits the password outright.
        // UseConnection() handles both: it derives the LISTEN-adjusted string, and - when needed -
        // a template connection that still authenticates correctly regardless.
        builder.UseConnection(context.Database.GetDbConnection());

        logger.LogInformation(
            "Derived {ChannelCount} notification channel mapping(s) from {DbContext}", channelCount, candidate.Name);
    }

    private IEnumerable<Type> ContextTypes()
    {
        return contextType is not null
            ? [contextType]
            : services
            .Select(descriptor => descriptor.ServiceType)
            .Where(static type => type.IsSubclassOf(typeof(DbContext)))
            .Distinct();
    }
}
