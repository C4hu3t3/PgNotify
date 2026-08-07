namespace PgNotify.Serialization;

/// <summary>
/// Parses the raw text payload of a received PostgreSQL notification into a
/// <see cref="NotificationEnvelope"/>. This is a runtime (.NET-side) concern, separate from the
/// SQL-side <see cref="Payloads.INotificationPayloadBuilder"/> that describes what the trigger
/// writes into the payload in the first place.
/// </summary>
/// <remarks>
/// The <see cref="DefaultNotificationPayloadDeserializer"/> understands the well-known field
/// names produced by <see cref="Payloads.MinimalNotificationPayloadBuilder"/> and
/// <see cref="Payloads.ExtendedNotificationPayloadBuilder"/>. A custom
/// <see cref="Payloads.INotificationPayloadBuilder"/> that emits different JSON keys needs a
/// matching custom implementation of this interface, registered via
/// <c>PostgresNotificationsOptions.PayloadDeserializer</c>.
/// </remarks>
public interface INotificationPayloadDeserializer
{
    /// <summary>Parses <paramref name="payload"/>, received on <paramref name="channel"/>, into an envelope.</summary>
    /// <exception cref="NotificationPayloadFormatException">The payload is not valid JSON, or is missing required fields.</exception>
    NotificationEnvelope Deserialize(string channel, string payload);
}
