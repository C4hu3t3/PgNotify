using System.Globalization;

namespace PgNotify.Naming;

/// <summary>
/// One channel per entity/operation pair, dot-separated and lower-cased (e.g.
/// <c>user.created</c>, <c>user.updated</c>, <c>user.deleted</c>). Lets subscribers listen to
/// only the operations they care about.
/// </summary>
public sealed class TopicChannelNamingStrategy : INotificationChannelNamingStrategy
{
    /// <summary>A shared, stateless instance using the default <c>"."</c> separator.</summary>
    public static readonly TopicChannelNamingStrategy Instance = new();

    private readonly string _separator;

    /// <summary>The separator this instance was constructed with.</summary>
    public string Separator => _separator;

    /// <summary>Creates a strategy using the given topic <paramref name="separator"/>.</summary>
    public TopicChannelNamingStrategy(string separator = ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(separator);
        _separator = separator;
    }

    /// <inheritdoc />
    public string GetChannelName(NotificationChannelNamingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var entitySegment = context.EntityDisplayName.ToLower(CultureInfo.InvariantCulture);
        var operationSegment = context.Operation.ToPastTenseWord();
        return PostgresIdentifier.EnsureWithinLength($"{entitySegment}{_separator}{operationSegment}");
    }
}
