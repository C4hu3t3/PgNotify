namespace PgNotify.Naming;

/// <summary>
/// One channel per entity, named after the mapped table (e.g. <c>users</c>, <c>orders</c>).
/// All operations for an entity share the same channel; the operation is carried in the payload.
/// This is the default strategy.
/// </summary>
public sealed class PerEntityChannelNamingStrategy : INotificationChannelNamingStrategy
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly PerEntityChannelNamingStrategy Instance = new();

    /// <inheritdoc />
    public string GetChannelName(NotificationChannelNamingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PostgresIdentifier.EnsureWithinLength(context.TableName);
    }
}
