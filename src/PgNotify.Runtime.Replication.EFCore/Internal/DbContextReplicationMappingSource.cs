using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PgNotify.Internal;

/// <summary>
/// Derives the replication listener's entities from the model of every registered <c>DbContext</c>
/// that has at least one <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured
/// entity, and offers that context's connection string as the listener's — the replication
/// counterpart to <c>DbContextNotificationMappingSource</c>.
/// </summary>
internal sealed class DbContextReplicationMappingSource(
    IServiceCollection services,
    Type? contextType,
    IServiceScopeFactory scopeFactory,
    ILogger<DbContextReplicationMappingSource> logger) : IReplicationMappingSource
{
    public Task ContributeAsync(ReplicationMappingBuilder builder, CancellationToken cancellationToken)
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
                if (contextType is not null)
                {
                    throw new InvalidOperationException(
                        $"'{candidate.Name}' was named as the source of the replication mapping, but it did not opt into " +
                        "notifications: chain UseNpgsqlNotifications() onto its UseNpgsql(...) call.");
                }

                continue;
            }

            Contribute(builder, context, candidate);
        }

        return Task.CompletedTask;
    }

    private void Contribute(ReplicationMappingBuilder builder, DbContext context, Type candidate)
    {
        var entityCount = 0;
        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.GetNotificationConfiguration() is not { DeliveryMode: NotificationDeliveryMode.LogicalReplication } config)
            {
                continue;
            }

            builder.AddEntity(config, entityType.ClrType);
            entityCount++;
        }

        if (entityCount == 0)
        {
            return;
        }

        builder.UseConnection(context.Database.GetDbConnection());

        logger.LogInformation(
            "Derived {EntityCount} logical replication entit(y/ies) from {DbContext}", entityCount, candidate.Name);
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
