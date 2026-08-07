namespace PgNotify.Payloads;

/// <summary>
/// Emits exactly the columns selected by <c>WithPayload(x =&gt; new { x.Status, x.Total })</c>,
/// alongside the entity name, the operation, and the row's key.
/// </summary>
/// <remarks>
/// The point of this shape is that the payload's JSON matches a typed event's own shape, rather
/// than the event having to match whichever payload a configuration default happened to pick —
/// which is how a <c>record OrderUpdated(int Id, string Status)</c> silently ends up with unbound
/// members otherwise.
/// </remarks>
/// <remarks>
/// The key is always emitted, even when the selector does not mention it: a notification that
/// cannot say which row changed is worse than no notification, and the selector is written by
/// someone thinking about payload contents, not about routing. It appears as a top-level
/// <c>"id"</c> for a single-column key and as a <c>"keys"</c> object otherwise, matching
/// <see cref="MinimalNotificationPayloadBuilder"/> so the reduced overflow payload stays a strict
/// subset of this one.
/// </remarks>
public sealed class ProjectedNotificationPayloadBuilder : INotificationPayloadBuilder
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly ProjectedNotificationPayloadBuilder Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var fields = new List<NotificationPayloadField>(context.PayloadColumns.Count + 3)
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

        foreach (var column in context.PayloadColumns)
        {
            // A key column named in the selector is already carried by the field above; emitting it
            // twice would produce a duplicate JSON member.
            if (context.KeyColumns.Count == 1 && column.ColumnName == context.KeyColumns[0])
            {
                continue;
            }

            // Property name as the JSON key, column name as the source: the payload is bound to a
            // .NET event type, so it must not inherit the storage layer's naming convention.
            fields.Add(NotificationPayloadField.Column(column.PropertyName, column.ColumnName));
        }

        return fields;
    }
}
