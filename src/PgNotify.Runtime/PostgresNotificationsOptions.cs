using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using PgNotify.Dispatch;
using PgNotify.Reconnect;
using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Configures <c>AddPostgresNotifications(...)</c>: connection, reconnect behavior, payload
/// parsing, which channels to listen on, which handlers to wire up, and the dispatch middleware
/// pipeline.
/// </summary>
public sealed class PostgresNotificationsOptions
{
    /// <summary>The PostgreSQL connection string for the dedicated LISTEN connection. Required.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The reconnect policy used when the LISTEN connection is lost. Defaults to <see cref="ExponentialBackoffReconnectPolicy"/>.</summary>
    public IReconnectPolicy ReconnectPolicy { get; set; } = new ExponentialBackoffReconnectPolicy();

    /// <summary>Parses raw notification payloads. Defaults to <see cref="DefaultNotificationPayloadDeserializer"/>.</summary>
    public INotificationPayloadDeserializer PayloadDeserializer { get; set; } = DefaultNotificationPayloadDeserializer.Instance;

    /// <summary>The maximum attempts <see cref="RetryNotificationMiddleware"/> makes before letting a dispatch failure propagate. Defaults to 3.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    internal List<Assembly> HandlerAssemblies { get; } = [];

    internal HashSet<Type> HandlerEntityTypes { get; } = [];

    internal List<(string Channel, Type? EntityType)> ChannelMappings { get; } = [];

    internal List<Func<IServiceCollection, IServiceProvider, INotificationMappingSource>> MappingSourceFactories { get; } = [];

    internal List<Type> MiddlewareTypes { get; } = [];

    internal bool ChangeTrackingEnabled { get; private set; }

    internal TimeSpan ChangeTrackingCoalesceWindow { get; private set; }

    /// <summary>
    /// Scans <paramref name="assembly"/> for notification handlers — <see cref="IDatabaseNotificationHandler"/>,
    /// <see cref="IDatabaseNotificationHandler{TEntity}"/>, <see cref="IDatabaseInsertedHandler{TEntity}"/>,
    /// <see cref="IDatabaseUpdatedHandler{TEntity}"/>, <see cref="IDatabaseDeletedHandler{TEntity}"/> —
    /// and registers each as a scoped service.
    /// </summary>
    /// <remarks>
    /// This registers handlers, not channels: an entity type says nothing about which channel
    /// carries it, so a handler only ever runs for a channel declared by
    /// <see cref="MapChannel{TEntity}(string)"/>.
    /// </remarks>
    public PostgresNotificationsOptions AddHandlersFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        HandlerAssemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// Listens on <paramref name="channel"/> and routes notifications reporting
    /// <typeparamref name="TEntity"/> to that type's handlers. Call it once per channel the entity
    /// publishes on — three times under the <c>topic</c> naming strategy, once under the others.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is null or whitespace.</exception>
    public PostgresNotificationsOptions MapChannel<TEntity>(string channel)
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ChannelMappings.Add((channel, typeof(TEntity)));
        return this;
    }

    /// <summary>
    /// Listens on <paramref name="channel"/> without binding it to an entity type: its
    /// notifications reach <see cref="IDatabaseNotificationHandler"/> only. This is how a listener
    /// with no CLR entity types to key on — another service's channel, a polyglot producer —
    /// subscribes.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is null or whitespace.</exception>
    public PostgresNotificationsOptions MapChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ChannelMappings.Add((channel, null));
        return this;
    }

    /// <summary>
    /// Enables <see cref="Caching.IEntityChangeTracker{TEntity}"/> /
    /// <see cref="Caching.IEntityChangeTrackerSource"/>, exposing every entity's last change as an
    /// <c>IChangeToken</c> for <c>IMemoryCache</c> expiration or <c>ETag</c>/<c>Last-Modified</c>
    /// generation. Only channels the runtime already listens on feed the trackers, so an entity
    /// tracked purely for caching — with no handler of its own — still needs its channel declared,
    /// via <see cref="MapChannel{TEntity}(string)"/> or model discovery.
    /// </summary>
    /// <param name="coalesceWindow">
    /// Collapses bursts (a bulk <c>UPDATE</c> emits one row-level <c>NOTIFY</c> per row) into at
    /// most one invalidation per window, plus the immediate leading one — trading up to
    /// <paramref name="coalesceWindow"/> of extra staleness for not stampeding the cache. Defaults
    /// to <see cref="TimeSpan.Zero"/>: invalidate on every notification.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="coalesceWindow"/> is negative.</exception>
    public PostgresNotificationsOptions AddChangeTracking(TimeSpan coalesceWindow = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(coalesceWindow, TimeSpan.Zero);

        if (!ChangeTrackingEnabled)
        {
            ChangeTrackingEnabled = true;
            UseMiddleware<Caching.ChangeTrackingNotificationMiddleware>();
        }

        ChangeTrackingCoalesceWindow = coalesceWindow;
        return this;
    }

    /// <summary>
    /// Registers a factory for an <see cref="INotificationMappingSource"/> that contributes channel
    /// mappings (and optionally the connection string) when the host starts. This is the
    /// extensibility point <c>AddNotificationMappingFromDbContexts()</c>/
    /// <c>AddNotificationMappingFromDbContext&lt;TContext&gt;()</c> (from
    /// <c>PgNotify.Runtime.EFCore</c>) are built on; call it directly only to plug in a source
    /// that has nothing to do with EF Core.
    /// </summary>
    /// <remarks>
    /// Takes the application's <see cref="IServiceCollection"/> as well as the built
    /// <see cref="IServiceProvider"/> because a source may need to enumerate what was registered
    /// (e.g. every <c>DbContext</c> type), which a built provider cannot answer — it resolves what
    /// it is asked for, it does not list it.
    /// </remarks>
    public PostgresNotificationsOptions AddMappingSource(Func<IServiceCollection, IServiceProvider, INotificationMappingSource> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        MappingSourceFactories.Add(factory);
        return this;
    }

    /// <summary>Adds a custom middleware stage, resolved from DI. Order matches call order.</summary>
    public PostgresNotificationsOptions UseMiddleware<TMiddleware>()
        where TMiddleware : class, INotificationMiddleware
    {
        MiddlewareTypes.Add(typeof(TMiddleware));
        return this;
    }

    /// <summary>Logs every notification received and its dispatch outcome/duration.</summary>
    public PostgresNotificationsOptions UseLogging() => UseMiddleware<LoggingNotificationMiddleware>();

    /// <summary>Retries a failed dispatch up to <see cref="MaxRetryAttempts"/> times with linear backoff.</summary>
    public PostgresNotificationsOptions UseRetry(int maxAttempts = 3)
    {
        MaxRetryAttempts = maxAttempts;
        return UseMiddleware<RetryNotificationMiddleware>();
    }

    /// <summary>Records throughput/latency metrics via <see cref="System.Diagnostics.Metrics"/>.</summary>
    public PostgresNotificationsOptions UseMetrics() => UseMiddleware<MetricsNotificationMiddleware>();
}
