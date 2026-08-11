using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// The publish-side counterpart to <see cref="IPostgresNotificationService"/>: lets a delivery
/// mechanism other than LISTEN/NOTIFY register entities and push already-built envelopes through
/// the same dispatch pipeline — same handlers, same <c>Events&lt;TEntity&gt;()</c> fan-out, same
/// <see cref="Dispatch.INotificationMiddleware"/> stack — that <c>PostgresNotificationHostedService</c>
/// uses for NOTIFY-delivered ones. <c>PgNotify.Runtime.Replication</c> is the motivating consumer
/// (see <c>docs/plans/logical-replication-delivery.md</c>), but nothing about this interface is
/// replication-specific: it is the general seam for "notifications that did not arrive via
/// LISTEN/NOTIFY."
/// </summary>
/// <remarks>
/// Registered automatically by <c>AddPostgresNotifications()</c>; nothing in
/// <c>PgNotify.Runtime</c> itself publishes through it. Requires <c>AddPostgresNotifications()</c>
/// to have been called — it shares that call's <c>NotificationChannelMap</c> and
/// <c>NotificationDispatchPipeline</c> singletons rather than owning its own, so a handler
/// registered once observes every delivery mechanism instead of only the one it happened to be
/// resolved to depend on.
/// </remarks>
public interface INotificationPublisher
{
    /// <summary>
    /// Registers <paramref name="entityType"/>'s dispatcher under <paramref name="channel"/>/
    /// <paramref name="entityName"/> — the same <c>(channel, entity)</c> key
    /// <see cref="NotificationEnvelope.Channel"/>/<see cref="NotificationEnvelope.Entity"/> must
    /// carry for <see cref="PublishAsync"/> to route to it — without adding <paramref name="channel"/>
    /// to what the LISTEN/NOTIFY listener actually <c>LISTEN</c>s on. Mapping the same pair twice is
    /// a no-op, matching <c>NotificationChannelMap.MapChannel</c>'s own idempotency.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="channel"/> or <paramref name="entityName"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Another type is already registered under the same channel and entity name.
    /// </exception>
    void RegisterEntity(string channel, string entityName, Type entityType);

    /// <summary>
    /// Runs <paramref name="envelope"/> through the same dispatch pipeline a LISTEN/NOTIFY-delivered
    /// notification uses: <c>Events&lt;TEntity&gt;()</c> subscribers, then the entity's
    /// operation-specific handlers, then its <see cref="IDatabaseNotificationHandler{TEntity}"/>
    /// handlers, then the non-generic <see cref="IDatabaseNotificationHandler"/> catch-alls — each
    /// awaited before the next starts, exactly as documented on <c>NotificationDispatchPipeline</c>.
    /// A <c>(channel, entity)</c> pair nothing registered via <see cref="RegisterEntity"/> still
    /// reaches the non-generic catch-alls; it just has no entity-keyed dispatcher to run first.
    /// </summary>
    Task PublishAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
