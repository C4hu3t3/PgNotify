using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Handles <see cref="NotificationOperation.Insert"/> notifications for
/// <typeparamref name="TEntity"/>. Past tense matches the payload's own vocabulary
/// (<c>"operation": "created"</c>): a handler reacts to something the database has already done.
/// </summary>
/// <typeparam name="TEntity">The mapped entity type — the routing key, not a payload shape.</typeparam>
public interface IDatabaseInsertedHandler<TEntity>
{
    /// <summary>Handles one insert notification for <typeparamref name="TEntity"/>.</summary>
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
