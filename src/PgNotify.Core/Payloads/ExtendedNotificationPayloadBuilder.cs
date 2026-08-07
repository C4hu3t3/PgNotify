namespace PgNotify.Payloads;

/// <summary>
/// Emits a full-context payload: <c>{"entity", "schema", "table", "operation", "keys", "changed",
/// "timestamp"}</c>.
/// </summary>
public sealed class ExtendedNotificationPayloadBuilder : INotificationPayloadBuilder
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly ExtendedNotificationPayloadBuilder Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return
        [
            NotificationPayloadField.Constant("entity", context.EntityDisplayName),
            NotificationPayloadField.Constant("schema", context.Schema ?? string.Empty),
            NotificationPayloadField.Constant("table", context.TableName),
            NotificationPayloadField.Of("operation", NotificationPayloadFieldKind.Operation),
            NotificationPayloadField.Of("keys", NotificationPayloadFieldKind.Keys),
            NotificationPayloadField.Of("changed", NotificationPayloadFieldKind.Changed),
            NotificationPayloadField.Of("timestamp", NotificationPayloadFieldKind.Timestamp),
        ];
    }
}
