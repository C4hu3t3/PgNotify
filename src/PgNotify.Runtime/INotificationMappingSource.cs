namespace PgNotify;

/// <summary>
/// Contributes channel mappings — and optionally the connection string — while the listener is
/// starting, rather than when it is registered. Implement this to derive what to <c>LISTEN</c> on
/// from something the container only knows about later, such as a <c>DbContext</c>'s model.
/// </summary>
/// <remarks>
/// Sources run inside a DI scope during <c>IHostedService.StartAsync</c>, before the listener
/// connects, and each one's contributions are merged with those declared inline by
/// <c>MapChannel(...)</c>. That timing is the point: <c>AddPostgresNotifications()</c> may be
/// called before whatever the mapping is derived from has been registered.
/// </remarks>
public interface INotificationMappingSource
{
    /// <summary>Adds this source's channels to <paramref name="builder"/>.</summary>
    Task ContributeAsync(NotificationMappingBuilder builder, CancellationToken cancellationToken);
}
