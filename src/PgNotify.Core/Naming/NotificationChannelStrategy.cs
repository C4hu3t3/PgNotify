namespace PgNotify.Naming;

/// <summary>
/// The built-in channel-naming strategies, selectable by name where an
/// <see cref="INotificationChannelNamingStrategy"/> instance cannot be passed — that is, from
/// <c>[NotifyChanges]</c>, whose arguments must be compile-time constants.
/// </summary>
public enum NotificationChannelStrategy
{
    /// <summary>One channel per entity, named after the mapped table. The default.</summary>
    PerEntity,

    /// <summary>One shared channel for every entity and operation.</summary>
    Single,

    /// <summary>One channel per entity/operation pair, e.g. <c>user.created</c>.</summary>
    Topic,
}
