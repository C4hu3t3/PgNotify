using System.Text.Json;

namespace PgNotify.Serialization;

/// <summary>
/// Parses the JSON payload shapes produced by the built-in
/// <see cref="Payloads.MinimalNotificationPayloadBuilder"/> and
/// <see cref="Payloads.ExtendedNotificationPayloadBuilder"/>: the well-known field names
/// <c>entity</c>, <c>operation</c>, <c>id</c>/<c>keys</c>, <c>changed</c>, and <c>timestamp</c>.
/// </summary>
public sealed class DefaultNotificationPayloadDeserializer : INotificationPayloadDeserializer
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly DefaultNotificationPayloadDeserializer Instance = new();

    /// <inheritdoc />
    public NotificationEnvelope Deserialize(string channel, string payload)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(payload);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new NotificationPayloadFormatException(channel, payload, "Notification payload is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new NotificationPayloadFormatException(channel, payload, "Notification payload must be a JSON object.");
            }

            var entity = ReadRequiredString(root, "entity", channel, payload);
            var operationText = ReadRequiredString(root, "operation", channel, payload);

            if (!NotificationOperationExtensions.TryParsePastTenseWord(operationText, out var operation))
            {
                throw new NotificationPayloadFormatException(
                    channel, payload, $"Unrecognized \"operation\" value '{operationText}'. Expected 'created', 'updated', or 'deleted'.");
            }

            var keys = ReadKeys(root);
            var changed = ReadChanged(root);
            var timestamp = ReadTimestamp(root);

            return new NotificationEnvelope
            {
                Channel = channel,
                Entity = entity,
                Operation = operation,
                Keys = keys,
                Changed = changed,
                Timestamp = timestamp,
                Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True,
                RawPayload = payload,
            };
        }
    }

    private static string ReadRequiredString(JsonElement root, string propertyName, string channel, string payload)
    {
        return !root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String
            ? throw new NotificationPayloadFormatException(
                channel, payload, $"Notification payload is missing required string field \"{propertyName}\".")
            : value.GetString()!;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadKeys(JsonElement root)
    {
        if (root.TryGetProperty("keys", out var keysElement) && keysElement.ValueKind == JsonValueKind.Object)
        {
            var keys = new Dictionary<string, JsonElement>();
            foreach (var property in keysElement.EnumerateObject())
            {
                keys[property.Name] = property.Value.Clone();
            }

            return keys;
        }

        return root.TryGetProperty("id", out var idElement)
            ? new Dictionary<string, JsonElement> { ["id"] = idElement.Clone() }
            : new Dictionary<string, JsonElement>();
    }

    private static string[] ReadChanged(JsonElement root)
    {
        return !root.TryGetProperty("changed", out var changedElement) || changedElement.ValueKind != JsonValueKind.Array
            ? []
            : [.. changedElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)];
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        return root.TryGetProperty("timestamp", out var timestampElement)
            && timestampElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timestampElement.GetString(), out var timestamp)
            ? timestamp
            : null;
    }
}
