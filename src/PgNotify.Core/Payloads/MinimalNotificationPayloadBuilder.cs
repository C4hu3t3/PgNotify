namespace PgNotify.Payloads;

/// <summary>
/// Emits the smallest useful payload: <c>{"entity": ..., "operation": ..., "id": ...}</c>.
/// </summary>
/// <remarks>
/// <c>"id"</c> is only well-defined for single-column primary keys. For composite keys this
/// builder automatically emits a <c>"keys"</c> object instead (see
/// <see cref="NotificationPayloadFieldKind.Keys"/>) so the payload always identifies the row
/// unambiguously; this fallback is documented behavior, not a bug.
/// </remarks>
public sealed class MinimalNotificationPayloadBuilder : INotificationPayloadBuilder
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly MinimalNotificationPayloadBuilder Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var fields = new List<NotificationPayloadField>
        {
            NotificationPayloadField.Constant("entity", context.EntityDisplayName),
            NotificationPayloadField.Of("operation", NotificationPayloadFieldKind.Operation),
        };

        if (context.KeyColumns.Count == 1)
        {
            fields.Add(NotificationPayloadField.Column("id", context.KeyColumns[0]));
        }
        else
        {
            fields.Add(NotificationPayloadField.Of("keys", NotificationPayloadFieldKind.Keys));
        }

        return fields;
    }
}
