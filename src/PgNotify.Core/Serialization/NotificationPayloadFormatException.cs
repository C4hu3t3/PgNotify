namespace PgNotify.Serialization;

/// <summary>
/// Thrown by an <see cref="INotificationPayloadDeserializer"/> when a received notification
/// payload cannot be parsed: malformed JSON, a missing required field, or an unrecognized
/// <c>"operation"</c> value. Distinguishing this from other exceptions lets the runtime dispatch
/// pipeline treat malformed payloads (a data problem, potentially caused by a schema/version
/// mismatch between the trigger and the listening application) differently from handler failures.
/// </summary>
/// <remarks>Creates a new <see cref="NotificationPayloadFormatException"/>.</remarks>
public sealed class NotificationPayloadFormatException(string channel, string rawPayload, string message, Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>The channel the malformed payload was received on.</summary>
    public string Channel { get; } = channel;

    /// <summary>The raw payload text that failed to parse.</summary>
    public string RawPayload { get; } = rawPayload;
}
