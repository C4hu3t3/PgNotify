using PgNotify;
using PgNotify.Serialization;

namespace PgNotify.Core.Tests.Serialization;

public class NotificationEnvelopeExtensionsTests
{
    private sealed record UserUpdated(int Id, string? Entity);

    [Fact]
    public void ToTyped_deserializes_raw_payload_with_case_insensitive_matching()
    {
        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize(
            "users", """{"entity":"User","operation":"updated","id":42}""");

        var typed = envelope.ToTyped<UserUpdated>();

        typed.Id.Should().Be(42);
        typed.Entity.Should().Be("User");
    }

    [Fact]
    public void ToTyped_throws_payload_format_exception_on_shape_mismatch()
    {
        var envelope = new NotificationEnvelope
        {
            Channel = "users",
            Entity = "User",
            Operation = NotificationOperation.Update,
            Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
            RawPayload = "not json at all",
        };

        var act = () => envelope.ToTyped<UserUpdated>();

        act.Should().Throw<NotificationPayloadFormatException>();
    }
}
