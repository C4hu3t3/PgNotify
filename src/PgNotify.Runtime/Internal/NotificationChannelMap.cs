using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// Holds the <c>channel ↔ entity type</c> map the runtime routes on, and the full set of channels
/// the listener needs to <c>LISTEN</c> on. Operations are deliberately absent from the map: the
/// payload states which one occurred, so the same entry serves an entity whose three operations
/// share one channel and one whose <c>topic</c> strategy gives each its own.
/// </summary>
/// <remarks>
/// The lookup key is <c>(channel, entity name)</c> rather than the channel alone, so a channel
/// shared by several entities (<c>SingleChannelNamingStrategy</c>) still routes to the right one —
/// the payload's <c>"entity"</c> field disambiguates it.
/// </remarks>
internal sealed class NotificationChannelMap
{
    private readonly Dictionary<(string Channel, string Entity), IEntityNotificationDispatcher> _dispatchers = [];
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Channels => _channels;

    /// <summary>Every entity type actually bound to a channel via one of the <c>MapChannel</c> overloads.</summary>
    public IEnumerable<Type> MappedEntityTypes => _dispatchers.Values.Select(d => d.EntityType).Distinct();

    /// <summary>
    /// Listens on <paramref name="channel"/> without binding it to an entity type: only
    /// <see cref="IDatabaseNotificationHandler"/> sees its notifications. This is the entry a
    /// listener with no CLR entity types (another service's channel, a polyglot producer) uses.
    /// </summary>
    public void MapChannel(string channel) => _channels.Add(channel);

    /// <summary>
    /// Listens on <paramref name="channel"/> and routes its notifications reporting
    /// <paramref name="entityType"/>'s name to that type's handlers. Mapping the same pair twice is
    /// a no-op, so merging maps derived from several sources is safe.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Two different types with the same name are mapped to the same channel. Nothing in the
    /// payload could tell them apart — the <c>"entity"</c> field carries the short name, not the
    /// namespace — so the mapping is rejected where it is declared rather than silently delivering
    /// one type's notifications to the other's handlers.
    /// </exception>
    public void MapChannel(string channel, Type entityType) => MapChannel(channel, entityType.Name, entityType);

    /// <summary>
    /// As <see cref="MapChannel(string, Type)"/>, but with the entity name stated separately: the
    /// payload carries the *model's* display name for the entity, which is the CLR type's name only
    /// as long as nothing renamed it (a shared-type entity, or an entity added by the string-named
    /// <c>ModelBuilder.Entity(string)</c> overload, does not agree).
    /// </summary>
    public void MapChannel(string channel, string entityName, Type entityType)
    {
        _channels.Add(channel);

        var key = (channel, entityName);
        if (_dispatchers.TryGetValue(key, out var existing))
        {
            if (existing.EntityType == entityType)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Channel '{channel}' is mapped to both '{existing.EntityType.FullName}' and '{entityType.FullName}'. " +
                $"They share the entity name '{entityName}', which is all a notification payload carries, " +
                "so notifications on that channel cannot be routed to one rather than the other. Give them separate channels.");
        }

        var dispatcherType = typeof(EntityNotificationDispatcher<>).MakeGenericType(entityType);
        _dispatchers[key] = (IEntityNotificationDispatcher)Activator.CreateInstance(dispatcherType)!;
    }

    public IEntityNotificationDispatcher? Find(NotificationEnvelope envelope) =>
        _dispatchers.GetValueOrDefault((envelope.Channel, envelope.Entity));
}
