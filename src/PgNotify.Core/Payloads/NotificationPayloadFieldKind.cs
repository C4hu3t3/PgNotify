namespace PgNotify.Payloads;

/// <summary>
/// What a single <see cref="NotificationPayloadField"/> contributes to the JSON payload built by
/// the trigger function. Kept as a closed set of SQL-expressible concepts (rather than an open
/// callback) so the <c>PgNotify.Migrations</c> SQL generator can turn a payload description
/// into a single deterministic <c>json_build_object(...)</c> call without executing arbitrary code.
/// </summary>
public enum NotificationPayloadFieldKind
{
    /// <summary>A fixed, compile-time-known string value (e.g. the entity display name).</summary>
    Constant,

    /// <summary>The operation that fired the trigger, as its <see cref="NotificationOperationExtensions.ToPastTenseWord"/> text.</summary>
    Operation,

    /// <summary>The mapped schema name, or <see langword="null"/> for the default schema.</summary>
    Schema,

    /// <summary>The mapped table name.</summary>
    Table,

    /// <summary>
    /// The value of a single named column from <c>NEW</c> (insert/update) or <c>OLD</c> (delete).
    /// Requires <see cref="NotificationPayloadField.ColumnName"/>.
    /// </summary>
    Column,

    /// <summary>
    /// A JSON object mapping every primary key column to its value. Always safe to use regardless
    /// of key cardinality (unlike <see cref="Column"/> against a single key column).
    /// </summary>
    Keys,

    /// <summary>
    /// A JSON array of the names of watched columns that actually changed, computed with
    /// <c>IS DISTINCT FROM</c>. Only meaningful for <see cref="NotificationOperation.Update"/>;
    /// empty for insert/delete.
    /// </summary>
    Changed,

    /// <summary>The wall-clock time the trigger fired, as an ISO-8601 timestamp (<c>clock_timestamp()</c>).</summary>
    Timestamp,
}
