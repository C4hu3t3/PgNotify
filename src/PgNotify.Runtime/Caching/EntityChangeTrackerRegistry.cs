using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PgNotify.Internal;

namespace PgNotify.Caching;

/// <summary>
/// Holds one <see cref="EntityChangeTracker"/> per entity name, created on first use — whether
/// that first use is a notification arriving or an <c>IEntityChangeTracker&lt;T&gt;</c> being
/// injected. Registered as a singleton so every DI scope and every consumer shares the same
/// tracker instance per entity.
/// </summary>
internal sealed class EntityChangeTrackerRegistry(TimeSpan coalesceWindow, NotificationChannelMap channelMap, ILogger<EntityChangeTrackerRegistry> logger)
    : IEntityChangeTrackerSource, IDisposable
{
    private readonly ConcurrentDictionary<string, EntityChangeTracker> _trackers = new(StringComparer.Ordinal);

    public IEntityChangeTracker Get(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return GetTracker(entityName, warnIfUnmapped: true);
    }

    /// <summary>
    /// Feeds every received notification, whether or not its entity is bound to a channel by type
    /// (see <see cref="ChangeTrackingNotificationMiddleware"/>) — so, unlike <see cref="Get"/>, this
    /// never warns: an entity name arriving here that isn't in <see cref="NotificationChannelMap.MappedEntityNames"/>
    /// is the expected shape for a channel declared with the non-generic <c>MapChannel(string)</c>,
    /// not a misconfiguration.
    /// </summary>
    public void MarkChanged(string entityName, DateTimeOffset changedAt) => GetTracker(entityName, warnIfUnmapped: false).MarkChanged(changedAt);

    private EntityChangeTracker GetTracker(string entityName, bool warnIfUnmapped)
    {
        if (warnIfUnmapped && channelMap.MappingResolved
            && !_trackers.ContainsKey(entityName) && !channelMap.MappedEntityNames.Contains(entityName))
        {
            logger.LogWarning(
                "IEntityChangeTracker<{EntityName}> was requested, but no channel maps notifications to " +
                "'{EntityName}'. Its GetChangeToken()/LastModified will never update. Declare a channel via " +
                "AddNotificationMappingFromDbContexts() or MapChannel<{EntityName}>(...), or stop injecting it " +
                "if the entity no longer has database notifications configured.",
                entityName, entityName, entityName);
        }

        return _trackers.GetOrAdd(entityName, static (name, state) => new EntityChangeTracker(name, state.Window, state.Logger), (Window: coalesceWindow, Logger: (ILogger)logger));
    }

    public void Dispose()
    {
        foreach (var tracker in _trackers.Values)
        {
            tracker.Dispose();
        }

        _trackers.Clear();
    }
}
