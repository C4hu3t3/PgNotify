using PgNotify;
using PgNotify.Internal;

// Intentionally in the Microsoft.Extensions.DependencyInjection namespace, matching where other
// AddXyz(...) DI registration entry points live.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the opt-in logical replication listener: a second delivery mechanism for
/// <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured entities, publishing
/// through the same dispatch pipeline <c>AddPostgresNotifications()</c> already set up.
/// </summary>
public static class LogicalReplicationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the logical replication listener with no inline configuration: everything — the
    /// connection string included — comes from the registered <see cref="IReplicationMappingSource"/>s,
    /// resolved when the host starts.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>AddPostgresNotifications()</c> was not called first.</exception>
    public static IServiceCollection AddPostgresLogicalReplication(this IServiceCollection services) =>
        services.AddPostgresLogicalReplication(static _ => { });

    /// <summary>Adds the logical replication listener, configured by <paramref name="configure"/>.</summary>
    /// <remarks>
    /// Requires <c>AddPostgresNotifications()</c> to already have been called: this listener
    /// publishes through that call's <c>NotificationChannelMap</c>/<c>NotificationDispatchPipeline</c>
    /// singletons (via <see cref="INotificationPublisher"/>) rather than owning its own, so a
    /// handler registered once observes both delivery mechanisms instead of only whichever one it
    /// happened to depend on.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>AddPostgresNotifications()</c> was not called first, or this was called twice.</exception>
    public static IServiceCollection AddPostgresLogicalReplication(
        this IServiceCollection services,
        Action<PostgresLogicalReplicationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(INotificationPublisher)))
        {
            throw new InvalidOperationException(
                $"{nameof(AddPostgresLogicalReplication)}() requires AddPostgresNotifications() to have been called " +
                "first on this service collection: the logical replication listener publishes through the same " +
                $"dispatch pipeline, via {nameof(INotificationPublisher)}, rather than owning a separate one.");
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(PostgresLogicalReplicationOptions)))
        {
            throw new InvalidOperationException(
                $"{nameof(AddPostgresLogicalReplication)}() was already called on this service collection. Calling " +
                "it a second time would register a second logical replication listener; call it once, and " +
                "configure everything through that single call.");
        }

        var options = new PostgresLogicalReplicationOptions();
        configure(options);

        foreach (var factory in options.MappingSourceFactories)
        {
            services.AddSingleton<IReplicationMappingSource>(sp => factory(services, sp));
        }

        services.AddSingleton(options);
        services.AddHostedService<LogicalReplicationHostedService>();

        return services;
    }
}
