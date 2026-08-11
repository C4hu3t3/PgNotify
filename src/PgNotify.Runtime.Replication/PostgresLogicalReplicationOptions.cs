using Microsoft.Extensions.DependencyInjection;
using PgNotify.Reconnect;

namespace PgNotify;

/// <summary>
/// Configures <c>AddPostgresLogicalReplication(...)</c>: connection, reconnect behavior, and which
/// entities to stream. Deliberately does not repeat handler scanning, middleware, or change
/// -tracking configuration from <see cref="PostgresNotificationsOptions"/> — a replication
/// -delivered notification runs through the exact same <c>NotificationDispatchPipeline</c> a
/// NOTIFY-delivered one does (see <see cref="INotificationPublisher"/>), so all of that is already
/// configured once, by <c>AddPostgresNotifications(...)</c>.
/// </summary>
public sealed class PostgresLogicalReplicationOptions
{
    /// <summary>
    /// The PostgreSQL connection string for the dedicated replication streaming connection.
    /// Required unless an <see cref="IReplicationMappingSource"/> supplies one.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>The reconnect policy used when the replication connection is lost. Defaults to <see cref="ExponentialBackoffReconnectPolicy"/>.</summary>
    public IReconnectPolicy ReconnectPolicy { get; set; } = new ExponentialBackoffReconnectPolicy();

    internal List<Func<IServiceCollection, IServiceProvider, IReplicationMappingSource>> MappingSourceFactories { get; } = [];

    /// <summary>
    /// Registers a factory for an <see cref="IReplicationMappingSource"/> that contributes
    /// replication-configured entities (and optionally the connection string) when the host starts.
    /// This is the extensibility point <c>AddReplicationMappingFromDbContexts()</c> (from
    /// <c>PgNotify.Runtime.EFCore</c>) is built on; call it directly only to plug in a source that
    /// has nothing to do with EF Core.
    /// </summary>
    public PostgresLogicalReplicationOptions AddMappingSource(Func<IServiceCollection, IServiceProvider, IReplicationMappingSource> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        MappingSourceFactories.Add(factory);
        return this;
    }
}
