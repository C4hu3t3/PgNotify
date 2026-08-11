using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PgNotify;
using PgNotify.Caching;
using PgNotify.Dispatch;
using PgNotify.Internal;
using PgNotify.Serialization;

// Intentionally in the Microsoft.Extensions.DependencyInjection namespace, matching where other
// AddXyz(...) DI registration entry points live.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the PostgreSQL notification runtime: the dedicated LISTEN connection, automatic
/// reconnect, the dispatch pipeline, entity-keyed handler/<c>Events&lt;TEntity&gt;()</c> support, and
/// a health check.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds PostgreSQL database notifications with no inline configuration: everything — the
    /// connection string included — comes from the registered
    /// <see cref="INotificationMappingSource"/>s, resolved when the host starts.
    /// </summary>
    public static IServiceCollection AddPostgresNotifications(this IServiceCollection services) =>
        services.AddPostgresNotifications(static _ => { });

    /// <summary>Adds PostgreSQL database notifications, configured by <paramref name="configure"/>.</summary>
    /// <remarks>
    /// A missing connection string is <i>not</i> rejected here: an
    /// <see cref="INotificationMappingSource"/> may supply one, and it may be registered after this
    /// call. It is settled — and, if still missing, reported — when the host starts.
    /// </remarks>
    public static IServiceCollection AddPostgresNotifications(
        this IServiceCollection services,
        Action<PostgresNotificationsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Every registration below is a bare AddSingleton with no dedup, so a second call would not
        // merge with the first - it would add a second PostgresNotificationHostedService that shares
        // the first's singleton NpgsqlNotificationListener (single-service resolution always returns
        // the last registration), and both instances would subscribe to its NotificationReceived
        // event independently. The result is silent: every notification dispatched twice, no
        // exception, no log. Rejecting the second call here is the same "loud on ambiguity" choice
        // NotificationChannelMap.MapChannel and ResolveConnectionString already make elsewhere in
        // this codebase.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PostgresNotificationsOptions)))
        {
            throw new InvalidOperationException(
                $"{nameof(AddPostgresNotifications)}() was already called on this service collection. Calling " +
                "it a second time would register a second notification listener and dispatch every " +
                "notification twice; call it once, and configure everything through that single call.");
        }

        var options = new PostgresNotificationsOptions();
        configure(options);

        foreach (var assembly in options.HandlerAssemblies)
        {
            HandlerAssemblyScanner.RegisterHandlersFromAssembly(assembly, services, options.HandlerEntityTypes);
        }

        var channelMap = new NotificationChannelMap();
        foreach (var (channel, entityType) in options.ChannelMappings)
        {
            if (entityType is null)
            {
                channelMap.MapChannel(channel);
            }
            else
            {
                channelMap.MapChannel(channel, entityType);
            }
        }

        foreach (var factory in options.MappingSourceFactories)
        {
            services.AddSingleton<INotificationMappingSource>(sp => factory(services, sp));
        }

        services.AddSingleton(options);
        services.AddSingleton<IOptions<PostgresNotificationsOptions>>(new OptionsWrapper<PostgresNotificationsOptions>(options));
        services.AddSingleton(channelMap);
        services.AddSingleton<NotificationEventHub>();
        services.AddSingleton<IPostgresNotificationService, PostgresNotificationService>();
        services.AddSingleton<INotificationPayloadDeserializer>(options.PayloadDeserializer);

        services.AddSingleton<NotificationRuntimeState>();
        services.AddSingleton(sp => new NpgsqlNotificationListener(
            sp.GetRequiredService<NotificationRuntimeState>(),
            options.ReconnectPolicy,
            sp.GetRequiredService<ILogger<NpgsqlNotificationListener>>()));

        foreach (var middlewareType in options.MiddlewareTypes)
        {
            services.AddSingleton(typeof(INotificationMiddleware), middlewareType);
        }

        if (options.ChangeTrackingEnabled)
        {
            services.AddSingleton(sp => new EntityChangeTrackerRegistry(
                options.ChangeTrackingCoalesceWindow,
                sp.GetRequiredService<NotificationChannelMap>(),
                sp.GetRequiredService<ILogger<EntityChangeTrackerRegistry>>()));
            services.AddSingleton<IEntityChangeTrackerSource>(sp => sp.GetRequiredService<EntityChangeTrackerRegistry>());
            services.AddSingleton(typeof(IEntityChangeTracker<>), typeof(EntityChangeTrackerOf<>));
        }

        services.AddSingleton<NotificationDispatchPipeline>();
        services.AddSingleton<INotificationPublisher, NotificationPublisher>();
        services.AddHostedService<PostgresNotificationHostedService>();

        services.AddHealthChecks().AddCheck<PostgresNotificationHealthCheck>("postgres-notifications");

        return services;
    }
}
