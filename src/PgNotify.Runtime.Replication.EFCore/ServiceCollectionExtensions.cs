using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PgNotify;
using PgNotify.Internal;

// Intentionally in the Microsoft.Extensions.DependencyInjection namespace, matching where other
// AddXyz(...) registration entry points live.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires the replication listener to the EF Core models that already describe what it should
/// stream, so table/publication/slot configuration is stated once — in the entity configuration —
/// instead of twice. The replication counterpart to <c>NotificationsEFCoreServiceCollectionExtensions</c>.
/// </summary>
public static class ReplicationEFCoreServiceCollectionExtensions
{
    /// <summary>
    /// Derives the replication mapping from every registered <c>DbContext</c> that has at least one
    /// <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured entity: which entities to
    /// stream, and — unless one was configured explicitly — its connection string, adjusted for a
    /// replication connection.
    /// </summary>
    public static PostgresLogicalReplicationOptions AddReplicationMappingFromDbContexts(this PostgresLogicalReplicationOptions options) =>
        options.AddDbContextMappingSource(contextType: null);

    /// <summary>
    /// Derives the replication mapping from <typeparamref name="TContext"/> alone, for a process
    /// that registers several contexts and wants one of them to drive the replication listener.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the host starts if <typeparamref name="TContext"/> did not chain
    /// <c>UseNpgsqlNotifications()</c>.
    /// </exception>
    public static PostgresLogicalReplicationOptions AddReplicationMappingFromDbContext<TContext>(this PostgresLogicalReplicationOptions options)
        where TContext : DbContext
        => options.AddDbContextMappingSource(typeof(TContext));

    private static PostgresLogicalReplicationOptions AddDbContextMappingSource(this PostgresLogicalReplicationOptions options, Type? contextType) =>
        options.AddMappingSource((services, sp) => new DbContextReplicationMappingSource(
            services,
            contextType,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<DbContextReplicationMappingSource>>()));
}
