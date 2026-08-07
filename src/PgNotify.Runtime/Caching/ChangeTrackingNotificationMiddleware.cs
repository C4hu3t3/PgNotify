using PgNotify.Dispatch;

namespace PgNotify.Caching;

/// <summary>
/// Feeds every received notification into <see cref="EntityChangeTrackerRegistry"/>. Added by
/// <c>options.AddChangeTracking()</c>.
/// </summary>
/// <remarks>
/// It is a middleware rather than a handler because it must see <i>every</i> notification, whether
/// or not the channel it arrived on maps to an entity type — an entity sharing a channel
/// (<c>SingleChannelNamingStrategy</c>), or a channel declared with the non-generic
/// <c>MapChannel("...")</c>, still has a name to track.
/// </remarks>
internal sealed class ChangeTrackingNotificationMiddleware(EntityChangeTrackerRegistry registry) : INotificationMiddleware
{
    public Task InvokeAsync(NotificationContext context, NotificationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // Marked before the rest of the pipeline, not after: a cache that survives a failed
        // dispatch is a stale cache, and marking after next() would additionally re-mark on every
        // RetryNotificationMiddleware attempt.
        registry.MarkChanged(context.Envelope.Entity, context.Envelope.Timestamp ?? DateTimeOffset.UtcNow);

        return next(context);
    }
}
