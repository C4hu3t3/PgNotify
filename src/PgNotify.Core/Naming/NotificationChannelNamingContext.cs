namespace PgNotify.Naming;

/// <summary>
/// The information a <see cref="INotificationChannelNamingStrategy"/> needs to compute a
/// PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> channel name for one entity/operation pair.
/// </summary>
/// <param name="EntityDisplayName">
/// The CLR entity type's simple name (e.g. <c>User</c>), used by strategies that want a
/// human-readable, PascalCase-derived name rather than the raw table name.
/// </param>
/// <param name="Schema">The mapped schema, or <see langword="null"/> for the default schema.</param>
/// <param name="TableName">The mapped table name (e.g. <c>users</c>).</param>
/// <param name="Operation">The operation the channel name is being computed for.</param>
public sealed record NotificationChannelNamingContext(
    string EntityDisplayName,
    string? Schema,
    string TableName,
    NotificationOperation Operation);
