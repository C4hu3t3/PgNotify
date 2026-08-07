using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// The public facade for consuming notifications as async streams:
/// <c>await foreach (var e in notifications.Events&lt;Product&gt;())</c>. For handler-class based
/// consumption, implement one of the entity-keyed handler interfaces
/// (<see cref="IDatabaseNotificationHandler{TEntity}"/> and friends) instead — both styles observe
/// the same notifications, and both are keyed on the entity type.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Events{TEntity}(CancellationToken)"/> has no history or replay: a notification
/// published before a particular call starts enumerating is gone for that call, by design — there
/// is no buffer holding it anywhere. This makes a startup race possible: the listener may already
/// be receiving and publishing notifications by the time application code gets around to calling
/// <c>Events&lt;TEntity&gt;()</c> for the first time (both hosted services' <c>StartAsync</c> runs
/// concurrently with no ordering guarantee between them), and whatever arrived in that window is
/// silently missed — no exception, no log.
/// </para>
/// <para>
/// The handler interfaces do not have this race: they are resolved from DI, whose registrations
/// exist before the host — and therefore the listener — starts at all. Prefer
/// <see cref="IDatabaseInsertedHandler{TEntity}"/>/<see cref="IDatabaseUpdatedHandler{TEntity}"/>/
/// <see cref="IDatabaseDeletedHandler{TEntity}"/>/<see cref="IDatabaseNotificationHandler{TEntity}"/>
/// over <c>Events&lt;TEntity&gt;()</c> whenever a notification from the moment the entity starts
/// being watched must not be missed.
/// </para>
/// </remarks>
public interface IPostgresNotificationService
{
    /// <summary>
    /// Streams every notification for <typeparamref name="TEntity"/> received after this call,
    /// until <paramref name="cancellationToken"/> is cancelled or enumeration stops. Multiple
    /// concurrent callers each receive every notification independently (a hot stream, not a shared
    /// queue). See the type-level remarks for why a notification published before this call starts
    /// enumerating is never delivered to it.
    /// </summary>
    /// <typeparam name="TEntity">
    /// The mapped entity type — the same routing key handlers use, so a stream only ever yields
    /// notifications from a channel declared for it.
    /// </typeparam>
    IAsyncEnumerable<NotificationEnvelope> Events<TEntity>(CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="Events{TEntity}(CancellationToken)"/>, but yielding only
    /// <paramref name="operation"/>.
    /// </summary>
    IAsyncEnumerable<NotificationEnvelope> Events<TEntity>(NotificationOperation operation, CancellationToken cancellationToken = default);
}
