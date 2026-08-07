using System.Text.Json;

namespace PgNotify.Serialization;

/// <summary>
/// Converts a generic <see cref="NotificationEnvelope"/> into a strongly-typed event object.
/// </summary>
public static class NotificationEnvelopeExtensions
{
    private static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deserializes <see cref="NotificationEnvelope.RawPayload"/> into <typeparamref name="T"/>
    /// using case-insensitive property matching. Works directly for payloads produced by
    /// <see cref="Payloads.MinimalNotificationPayloadBuilder"/> (e.g. a type with an <c>Id</c>
    /// property matches its top-level <c>"id"</c> field) or a custom payload builder whose JSON
    /// shape matches <typeparamref name="T"/>.
    /// </summary>
    public static T ToTyped<T>(this NotificationEnvelope envelope, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        try
        {
            return JsonSerializer.Deserialize<T>(envelope.RawPayload, options ?? DefaultOptions)
                ?? throw new NotificationPayloadFormatException(envelope.Channel, envelope.RawPayload, $"Payload deserialized to null for type {typeof(T)}.");
        }
        catch (JsonException ex)
        {
            throw new NotificationPayloadFormatException(
                envelope.Channel, envelope.RawPayload, $"Failed to deserialize notification payload as {typeof(T)}.", ex);
        }
    }
}
