using System.Collections.Concurrent;
using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// Holds one <see cref="EventBroadcaster{T}"/> per entity type, created when something first
/// subscribes to it. Registered as a singleton so all DI scopes (each notification gets its own
/// scope) publish to, and <c>Events&lt;TEntity&gt;()</c> subscribers read from, the same broadcasters.
/// </summary>
internal sealed class NotificationEventHub
{
    private readonly ConcurrentDictionary<Type, EventBroadcaster<NotificationEnvelope>> _broadcasters = new();

    public IAsyncEnumerable<NotificationEnvelope> Subscribe(Type entityType, CancellationToken cancellationToken) =>
        _broadcasters.GetOrAdd(entityType, static _ => new EventBroadcaster<NotificationEnvelope>()).Subscribe(cancellationToken);

    /// <summary>
    /// Publishes to <paramref name="entityType"/>'s subscribers, if any. A broadcaster only exists
    /// once something has subscribed, so an entity nobody streams costs one dictionary lookup per
    /// notification and nothing else.
    /// </summary>
    public void Publish(Type entityType, NotificationEnvelope envelope)
    {
        if (_broadcasters.TryGetValue(entityType, out var broadcaster))
        {
            broadcaster.Publish(envelope);
        }
    }
}
