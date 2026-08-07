namespace PgNotify;

/// <summary>
/// A single DML operation that raised a notification. Unlike <see cref="NotificationOperations"/>
/// (a configuration-time flags set), this identifies exactly one occurrence: the operation carried
/// on a <see cref="Model.NotificationEntityConfiguration"/>-derived trigger firing, or on a received
/// <see cref="Serialization.NotificationEnvelope"/>.
/// </summary>
public enum NotificationOperation
{
    /// <summary>A row was inserted.</summary>
    Insert,

    /// <summary>A row was updated.</summary>
    Update,

    /// <summary>A row was deleted.</summary>
    Delete,
}

/// <summary>
/// Conversions between <see cref="NotificationOperation"/>/<see cref="NotificationOperations"/> and
/// their textual representations used in SQL and JSON payloads.
/// </summary>
public static class NotificationOperationExtensions
{
    /// <summary>Converts a single operation to its <see cref="NotificationOperations"/> flag.</summary>
    public static NotificationOperations ToFlag(this NotificationOperation operation) => operation switch
    {
        NotificationOperation.Insert => NotificationOperations.Insert,
        NotificationOperation.Update => NotificationOperations.Update,
        NotificationOperation.Delete => NotificationOperations.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, message: null),
    };

    /// <summary>
    /// Expands a <see cref="NotificationOperations"/> flags set into its individual, configured
    /// <see cref="NotificationOperation"/> values, in <c>Insert, Update, Delete</c> order.
    /// </summary>
    public static IEnumerable<NotificationOperation> Expand(this NotificationOperations operations)
    {
        if (operations.HasFlag(NotificationOperations.Insert))
        {
            yield return NotificationOperation.Insert;
        }

        if (operations.HasFlag(NotificationOperations.Update))
        {
            yield return NotificationOperation.Update;
        }

        if (operations.HasFlag(NotificationOperations.Delete))
        {
            yield return NotificationOperation.Delete;
        }
    }

    /// <summary>The upper-case SQL trigger event keyword (<c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>).</summary>
    public static string ToSqlKeyword(this NotificationOperation operation) => operation switch
    {
        NotificationOperation.Insert => "INSERT",
        NotificationOperation.Update => "UPDATE",
        NotificationOperation.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, message: null),
    };

    /// <summary>The lower-case, past-tense word used by the default JSON payload and topic channel naming (<c>created</c>, <c>updated</c>, <c>deleted</c>).</summary>
    public static string ToPastTenseWord(this NotificationOperation operation) => operation switch
    {
        NotificationOperation.Insert => "created",
        NotificationOperation.Update => "updated",
        NotificationOperation.Delete => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, message: null),
    };

    /// <summary>Parses the SQL <c>TG_OP</c> trigger variable value (<c>INSERT</c>/<c>UPDATE</c>/<c>DELETE</c>) back into a <see cref="NotificationOperation"/>.</summary>
    public static NotificationOperation ParseSqlKeyword(string tgOp) => tgOp switch
    {
        "INSERT" => NotificationOperation.Insert,
        "UPDATE" => NotificationOperation.Update,
        "DELETE" => NotificationOperation.Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(tgOp), tgOp, message: "Unrecognized TG_OP value."),
    };

    /// <summary>Attempts to parse a payload <c>"operation"</c> field value (<c>created</c>/<c>updated</c>/<c>deleted</c>, case-insensitive).</summary>
    public static bool TryParsePastTenseWord(string? value, out NotificationOperation operation)
    {
        switch (value?.ToLowerInvariant())
        {
            case "created":
                operation = NotificationOperation.Insert;
                return true;
            case "updated":
                operation = NotificationOperation.Update;
                return true;
            case "deleted":
                operation = NotificationOperation.Delete;
                return true;
            default:
                operation = default;
                return false;
        }
    }
}
