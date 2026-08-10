using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PgNotify;
using PgNotify.Internal;

// Intentionally in the Microsoft.Extensions.DependencyInjection namespace, matching where other
// AddXyz(...) registration entry points live.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wires the runtime listener to the EF Core models that already describe what it should listen
/// to, so channel names are stated once — in the entity configuration — instead of twice.
/// </summary>
/// <remarks>
/// Both methods extend <see cref="PostgresNotificationsOptions"/>, not <see cref="IServiceCollection"/>
/// directly, so they only exist inside <c>AddPostgresNotifications(options => ...)</c> — there is no
/// way to register a mapping source that nothing ever consumes because
/// <c>AddPostgresNotifications()</c> was never called.
/// </remarks>
public static class NotificationsEFCoreServiceCollectionExtensions
{
    /// <summary>
    /// Derives the notification mapping from every registered <c>DbContext</c> that chained
    /// <c>UseNpgsqlNotifications()</c>: its channels, which entity type each carries, and — unless
    /// one was configured explicitly — its connection string, adjusted for a <c>LISTEN</c>
    /// connection.
    /// </summary>
    /// <remarks>
    /// Order-independent with respect to <c>AddDbContext</c>/<c>AddDbContextFactory</c>/
    /// <c>AddPooledDbContextFactory</c>, because the models are read when the host starts, not
    /// here. Since EF Core 8, <c>AddDbContextFactory</c>/<c>AddPooledDbContextFactory</c> also
    /// register <c>TContext</c> itself as a scoped service alongside
    /// <c>IDbContextFactory&lt;TContext&gt;</c>, so a context registered only that way is still
    /// discovered here — no need to fall back to
    /// <see cref="AddNotificationMappingFromDbContext{TContext}"/> for that reason alone.
    /// </remarks>
    public static PostgresNotificationsOptions AddNotificationMappingFromDbContexts(this PostgresNotificationsOptions options) =>
        options.AddDbContextMappingSource(contextType: null);

    /// <summary>
    /// Derives the notification mapping from <typeparamref name="TContext"/> alone, for a process
    /// that registers several contexts and wants one of them to drive the listener.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the host starts if <typeparamref name="TContext"/> did not chain
    /// <c>UseNpgsqlNotifications()</c> — naming a context that has nothing to contribute is a
    /// mistake worth reporting, whereas skipping one during discovery is not.
    /// </exception>
    public static PostgresNotificationsOptions AddNotificationMappingFromDbContext<TContext>(this PostgresNotificationsOptions options)
        where TContext : DbContext
        => options.AddDbContextMappingSource(typeof(TContext));

    private static PostgresNotificationsOptions AddDbContextMappingSource(this PostgresNotificationsOptions options, Type? contextType) =>
        options.AddMappingSource((services, sp) => new DbContextNotificationMappingSource(
            services,
            contextType,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<DbContextNotificationMappingSource>>()));
}
