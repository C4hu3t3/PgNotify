using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Handles <see cref="NotificationOperation.Update"/> notifications for
/// <typeparamref name="TEntity"/>. Which columns actually changed is on
/// <see cref="NotificationEnvelope.Changed"/> — populated by the extended payload only.
/// </summary>
/// <typeparam name="TEntity">The mapped entity type — the routing key, not a payload shape.</typeparam>
public interface IDatabaseUpdatedHandler<TEntity>
{
    /// <summary>Handles one update notification for <typeparamref name="TEntity"/>.</summary>
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
