using PgNotify;
using PgNotify.Serialization;

namespace PgNotify.Core.Tests.Serialization;

public class DefaultNotificationPayloadDeserializerTests
{
    private readonly DefaultNotificationPayloadDeserializer _sut = DefaultNotificationPayloadDeserializer.Instance;

    [Fact]
    public void Deserialize_parses_minimal_payload_shape()
    {
        var envelope = _sut.Deserialize("users", """{"entity":"User","operation":"updated","id":42}""");

        envelope.Channel.Should().Be("users");
        envelope.Entity.Should().Be("User");
        envelope.Operation.Should().Be(NotificationOperation.Update);
        envelope.Keys.Should().ContainKey("id");
        envelope.Keys["id"].GetInt32().Should().Be(42);
        envelope.Changed.Should().BeEmpty();
        envelope.Timestamp.Should().BeNull();
    }

    [Fact]
    public void Deserialize_parses_extended_payload_shape()
    {
        const string payload = """
            {
              "entity": "User",
              "schema": "public",
              "table": "users",
              "operation": "updated",
              "keys": { "Id": 42 },
              "changed": ["Name", "Email"],
              "timestamp": "2026-07-24T12:00:00Z"
            }
            """;

        var envelope = _sut.Deserialize("users", payload);

        envelope.Keys.Should().ContainKey("Id");
        envelope.Keys["Id"].GetInt32().Should().Be(42);
        envelope.Changed.Should().Equal("Name", "Email");
        envelope.Timestamp.Should().Be(DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
    }

    [Fact]
    public void Deserialize_reads_the_truncated_flag_of_a_reduced_payload()
    {
        // What the trigger sends instead of a full payload that would have exceeded pg_notify's
        // limit and aborted the write (see NotificationPayloadOverflow.Truncate).
        var envelope = _sut.Deserialize(
            "users", """{"entity":"User","operation":"created","id":42,"truncated":true}""");

        envelope.Truncated.Should().BeTrue();
        envelope.Keys["id"].GetInt32().Should().Be(42, "the key is the one thing a reduced payload keeps");
    }

    [Fact]
    public void Deserialize_reports_a_complete_payload_as_not_truncated()
    {
        var envelope = _sut.Deserialize("users", """{"entity":"User","operation":"created","id":42}""");

        envelope.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_throws_payload_format_exception_for_invalid_json()
    {
        var act = () => _sut.Deserialize("users", "not json");

        act.Should().Throw<NotificationPayloadFormatException>()
            .Which.Channel.Should().Be("users");
    }

    [Fact]
    public void Deserialize_throws_for_missing_entity_field()
    {
        var act = () => _sut.Deserialize("users", """{"operation":"created","id":1}""");

        act.Should().Throw<NotificationPayloadFormatException>();
    }

    [Fact]
    public void Deserialize_throws_for_unrecognized_operation()
    {
        var act = () => _sut.Deserialize("users", """{"entity":"User","operation":"bogus","id":1}""");

        act.Should().Throw<NotificationPayloadFormatException>();
    }

    [Fact]
    public void Deserialize_returns_empty_keys_when_neither_id_nor_keys_present()
    {
        var envelope = _sut.Deserialize("users", """{"entity":"User","operation":"deleted"}""");

        envelope.Keys.Should().BeEmpty();
    }
}
