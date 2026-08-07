namespace PgNotify.Payloads;

/// <summary>
/// One of the two built-in payload shapes, selected with
/// <c>WithPayload(NotificationPayloadKind.Minimal)</c>.
/// </summary>
/// <remarks>
/// Deliberately does not have a <c>Custom</c> member, unlike the internal discriminator it maps
/// onto: a custom payload cannot be named without also naming its builder type, which is what
/// <c>WithPayload&lt;TBuilder&gt;()</c> is for. A projection is likewise selected by its own
/// overload, <c>WithPayload(x =&gt; new { ... })</c>, since it needs the property list.
/// </remarks>
public enum NotificationPayloadKind
{
    /// <summary>
    /// <c>{"entity", "operation", "id"}</c> — see <see cref="MinimalNotificationPayloadBuilder"/>.
    /// The default when no payload is configured.
    /// </summary>
    Minimal,

    /// <summary>
    /// <c>{"entity", "schema", "table", "operation", "keys", "changed", "timestamp"}</c> — see
    /// <see cref="ExtendedNotificationPayloadBuilder"/>.
    /// </summary>
    Extended,
}
