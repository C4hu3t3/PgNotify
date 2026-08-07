using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Handles <see cref="NotificationOperation.Delete"/> notifications for
/// <typeparamref name="TEntity"/>. The row is already gone by the time this runs, so
/// <see cref="NotificationEnvelope.Keys"/> — built from <c>OLD</c> — is the only way to identify
/// it; re-reading it from the database is not an option.
/// </summary>
/// <typeparam name="TEntity">The mapped entity type — the routing key, not a payload shape.</typeparam>
public interface IDatabaseDeletedHandler<TEntity>
{
    /// <summary>Handles one delete notification for <typeparamref name="TEntity"/>.</summary>
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
