using Microsoft.Extensions.DependencyInjection;
using PgNotify.Dispatch;
using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// The default <see cref="INotificationPublisher"/>: a thin wrapper over the same
/// <see cref="NotificationChannelMap"/>/<see cref="NotificationDispatchPipeline"/> singletons
/// <c>PostgresNotificationHostedService</c> uses, so a notification's delivery mechanism is
/// invisible past this point.
/// </summary>
internal sealed class NotificationPublisher(
    NotificationChannelMap channelMap,
    NotificationDispatchPipeline pipeline,
    IServiceScopeFactory scopeFactory) : INotificationPublisher
{
    /// <inheritdoc />
    public void RegisterEntity(string channel, string entityName, Type entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(entityType);

        channelMap.RegisterDispatcher(channel, entityName, entityType);
    }

    /// <inheritdoc />
    public async Task PublishAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // A dedicated scope per notification, exactly matching PostgresNotificationHostedService.
        // OnNotificationReceivedAsync, so handlers here get the same scoped-service semantics
        // (a fresh DbContext, most of all) regardless of which delivery mechanism produced the
        // envelope.
        using var scope = scopeFactory.CreateScope();

        var context = new NotificationContext
        {
            Envelope = envelope,
            Services = scope.ServiceProvider,
            CancellationToken = cancellationToken,
        };

        await pipeline.InvokeAsync(context).ConfigureAwait(false);
    }
}
