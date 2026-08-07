namespace PgNotify.Payloads;

/// <summary>
/// One <c>"key": value</c> pair the trigger function should emit into the notification's
/// <c>json_build_object(...)</c> payload.
/// </summary>
/// <param name="JsonKey">The JSON property name (e.g. <c>"entity"</c>, <c>"id"</c>).</param>
/// <param name="Kind">What kind of value this field contributes.</param>
/// <param name="ColumnName">
/// The mapped column name to read from <c>NEW</c>/<c>OLD</c>. Required when
/// <paramref name="Kind"/> is <see cref="NotificationPayloadFieldKind.Column"/>, ignored otherwise.
/// </param>
/// <param name="ConstantValue">
/// The literal value to emit. Required when <paramref name="Kind"/> is
/// <see cref="NotificationPayloadFieldKind.Constant"/>, ignored otherwise.
/// </param>
public sealed record NotificationPayloadField(
    string JsonKey,
    NotificationPayloadFieldKind Kind,
    string? ColumnName = null,
    string? ConstantValue = null)
{
    /// <summary>Creates a <see cref="NotificationPayloadFieldKind.Constant"/> field.</summary>
    public static NotificationPayloadField Constant(string jsonKey, string value) =>
        new(jsonKey, NotificationPayloadFieldKind.Constant, ConstantValue: value);

    /// <summary>Creates a <see cref="NotificationPayloadFieldKind.Column"/> field.</summary>
    public static NotificationPayloadField Column(string jsonKey, string columnName) =>
        new(jsonKey, NotificationPayloadFieldKind.Column, ColumnName: columnName);

    /// <summary>Creates a field of any kind that does not require <see cref="ColumnName"/> or <see cref="ConstantValue"/>.</summary>
    public static NotificationPayloadField Of(string jsonKey, NotificationPayloadFieldKind kind) => new(jsonKey, kind);
}
