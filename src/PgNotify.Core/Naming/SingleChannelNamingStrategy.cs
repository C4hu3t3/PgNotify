namespace PgNotify.Naming;

/// <summary>
/// One shared channel (e.g. <c>entity_changed</c>) for every entity and operation. The entity
/// name and operation are carried in the payload so subscribers can filter after receiving.
/// Useful when a single process wants to observe all database activity through one subscription.
/// </summary>
public sealed class SingleChannelNamingStrategy : INotificationChannelNamingStrategy
{
    /// <summary>The default shared channel name.</summary>
    public const string DefaultChannelName = "entity_changed";

    private readonly string _channelName;

    /// <summary>Creates a strategy that always returns <paramref name="channelName"/>.</summary>
    public SingleChannelNamingStrategy(string channelName = DefaultChannelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        _channelName = PostgresIdentifier.EnsureWithinLength(channelName);
    }

    /// <inheritdoc />
    public string GetChannelName(NotificationChannelNamingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _channelName;
    }
}
