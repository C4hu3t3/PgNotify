using System.Text.Json;

namespace PgNotify.Serialization;

/// <summary>
/// A parsed PostgreSQL notification: the channel it arrived on plus the fields understood from
/// its JSON payload by convention (see <see cref="DefaultNotificationPayloadDeserializer"/>).
/// </summary>
public sealed record NotificationEnvelope
{
    /// <summary>The channel the notification was received on.</summary>
    public required string Channel { get; init; }

    /// <summary>The entity display name from the payload's <c>"entity"</c> field.</summary>
    public required string Entity { get; init; }

    /// <summary>The operation from the payload's <c>"operation"</c> field.</summary>
    public required NotificationOperation Operation { get; init; }

    /// <summary>
    /// The primary key value(s) identifying the affected row, keyed by column/property name.
    /// Populated from a payload <c>"keys"</c> object, or synthesized from a scalar <c>"id"</c>
    /// field (see <see cref="Payloads.MinimalNotificationPayloadBuilder"/>) under the key <c>"id"</c>.
    /// </summary>
    public required IReadOnlyDictionary<string, JsonElement> Keys { get; init; }

    /// <summary>The column names reported as changed, from the payload's <c>"changed"</c> array. Empty if absent.</summary>
    public IReadOnlyList<string> Changed { get; init; } = [];

    /// <summary>The trigger-reported timestamp from the payload's <c>"timestamp"</c> field, if present.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// <see langword="true"/> when the trigger replaced the configured payload with a reduced one
    /// because the full payload would have exceeded <c>pg_notify</c>'s 7999-byte limit (from the
    /// payload's <c>"truncated"</c> field, see
    /// <see cref="Payloads.NotificationPayloadOverflow.Truncate"/>). Only <see cref="Keys"/> can be
    /// relied on in that case — everything else the payload would normally carry is absent, so a
    /// handler that needs it must re-read the row.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>The raw, unparsed JSON payload text as received from PostgreSQL.</summary>
    public required string RawPayload { get; init; }
}
