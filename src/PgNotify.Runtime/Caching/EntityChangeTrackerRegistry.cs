using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PgNotify.Caching;

/// <summary>
/// Holds one <see cref="EntityChangeTracker"/> per entity name, created on first use — whether
/// that first use is a notification arriving or an <c>IEntityChangeTracker&lt;T&gt;</c> being
/// injected. Registered as a singleton so every DI scope and every consumer shares the same
/// tracker instance per entity.
/// </summary>
internal sealed class EntityChangeTrackerRegistry(TimeSpan coalesceWindow, ILogger<EntityChangeTrackerRegistry> logger)
    : IEntityChangeTrackerSource, IDisposable
{
    private readonly ConcurrentDictionary<string, EntityChangeTracker> _trackers = new(StringComparer.Ordinal);

    public IEntityChangeTracker Get(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        return GetTracker(entityName);
    }

    public void MarkChanged(string entityName, DateTimeOffset changedAt) => GetTracker(entityName).MarkChanged(changedAt);

    private EntityChangeTracker GetTracker(string entityName) =>
        _trackers.GetOrAdd(entityName, static (name, state) => new EntityChangeTracker(name, state.Window, state.Logger), (Window: coalesceWindow, Logger: (ILogger)logger));

    public void Dispose()
    {
        foreach (var tracker in _trackers.Values)
        {
            tracker.Dispose();
        }

        _trackers.Clear();
    }
}
