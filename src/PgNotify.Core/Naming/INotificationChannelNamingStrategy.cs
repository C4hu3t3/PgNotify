namespace PgNotify.Naming;

/// <summary>
/// Computes the PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> channel name used for a given entity and
/// operation. Implementations must be deterministic and pure: the same context must always
/// produce the same channel name, since migrations and the runtime listener each compute it
/// independently and must agree.
/// </summary>
/// <remarks>
/// Built-in strategies: <see cref="PerEntityChannelNamingStrategy"/> (one channel per entity),
/// <see cref="SingleChannelNamingStrategy"/> (one channel for every entity/operation), and
/// <see cref="TopicChannelNamingStrategy"/> (one channel per entity/operation pair, dot-separated).
/// </remarks>
public interface INotificationChannelNamingStrategy
{
    /// <summary>Computes the channel name for the given context.</summary>
    string GetChannelName(NotificationChannelNamingContext context);
}
