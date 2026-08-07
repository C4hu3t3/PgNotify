namespace PgNotify.Payloads;

/// <summary>
/// The entity metadata a <see cref="INotificationPayloadBuilder"/> needs to decide which
/// <see cref="NotificationPayloadField"/>s to emit. Supplied by <c>PgNotify.Migrations</c>
/// when generating trigger SQL, derived entirely from EF Core's model.
/// </summary>
/// <param name="EntityDisplayName">The CLR entity type's simple name (e.g. <c>User</c>).</param>
/// <param name="Schema">The mapped schema, or <see langword="null"/> for the default schema.</param>
/// <param name="TableName">The mapped table name.</param>
/// <param name="KeyColumns">The primary key column names, in key order.</param>
/// <param name="WatchedUpdateColumns">
/// The column names configured via <c>OnUpdate(x => ...)</c>. Empty means "watch every mapped
/// column" (the default when no property selector is given).
/// </param>
/// <param name="PayloadColumns">
/// The columns selected by <c>WithPayload(x => new { ... })</c>, in the order written, each
/// carrying both the CLR property that named it and the column its value is read from. Empty for
/// every payload shape that is not a projection. Carried on the context rather than held by the
/// builder because builders are reconstructed from annotations and must stay stateless — see
/// <see cref="ProjectedNotificationPayloadBuilder"/>.
/// </param>
public sealed record NotificationPayloadBuilderContext(
    string EntityDisplayName,
    string? Schema,
    string TableName,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> WatchedUpdateColumns,
    IReadOnlyList<NotificationPayloadColumn> PayloadColumns);
