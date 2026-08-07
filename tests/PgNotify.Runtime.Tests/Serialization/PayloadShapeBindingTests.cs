using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Serialization;

/// <summary>
/// Locks down how each payload shape binds into a typed event, because the shape is chosen at
/// configuration time while the event type is written somewhere else entirely — and getting the
/// pairing wrong fails silently, with no exception anywhere.
/// </summary>
/// <remarks>
/// <c>ToTyped&lt;T&gt;()</c> deserializes the raw payload as-is, so a
/// <c>record X(int Id)</c> binds only against a payload with a <b>top-level</b> <c>"id"</c>. The
/// minimal and projected shapes emit one; the extended shape nests the key under <c>"keys"</c> and
/// therefore leaves <c>Id</c> at its default. Both configuration styles now default to the minimal
/// shape for that reason — they used to disagree, and moving an entity between them quietly produced
/// events with no key.
/// </remarks>
public class PayloadShapeBindingTests
{
    /// <summary>The shape a consumer writes by hand for the minimal payload, and passes to <c>ToTyped&lt;T&gt;()</c>.</summary>
    private sealed record UserCreated(int Id);

    // All three are copied from SQL the generated trigger functions actually emit, down to
    // json_build_object's " : " spacing.
    private const string MinimalPayload =
        """{"entity" : "User", "operation" : "created", "id" : 42}""";

    private const string ExtendedPayload =
        """{"entity" : "User", "schema" : "", "table" : "users", "operation" : "created", "keys" : {"Id" : 42}, "changed" : [], "timestamp" : "2026-08-01T19:25:07.394938+00:00"}""";

    private const string ProjectedPayload =
        """{"entity" : "User", "operation" : "created", "id" : 42, "Name" : "Ada"}""";

    private static NotificationEnvelope Deserialize(string payload) =>
        DefaultNotificationPayloadDeserializer.Instance.Deserialize("users", payload);

    [Fact]
    public void A_minimal_payload_binds_its_top_level_id()
    {
        Deserialize(MinimalPayload).ToTyped<UserCreated>().Id.Should().Be(42);
    }

    [Fact]
    public void A_projected_payload_binds_too_and_ignores_columns_the_event_does_not_declare()
    {
        // A projection emits the key at the top level for exactly this reason.
        Deserialize(ProjectedPayload).ToTyped<UserCreated>().Id.Should().Be(42);
    }

    [Fact]
    public void An_extended_payload_leaves_a_top_level_Id_unbound()
    {
        // Not an assertion of desirable behavior, but of the actual one - and of its silence: no
        // exception, no log, just a default value. Configure WithPayload(NotificationPayloadKind
        // .Minimal) or WithPayload(x => ...), or declare an event whose shape matches the extended
        // payload.
        Deserialize(ExtendedPayload).ToTyped<UserCreated>().Id.Should().Be(0);
    }

    [Fact]
    public void An_extended_payload_still_carries_the_key_on_the_envelope()
    {
        // The key is not lost, only absent from the typed projection: the deserializer normalizes
        // both shapes into Keys, which is where a handler working against the extended payload
        // should read it.
        var envelope = Deserialize(ExtendedPayload);

        envelope.Keys.Should().ContainKey("Id");
        envelope.Keys["Id"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void A_minimal_payload_normalizes_its_id_onto_the_envelope_too()
    {
        var envelope = Deserialize(MinimalPayload);

        envelope.Keys.Should().ContainKey("id");
        envelope.Keys["id"].GetInt32().Should().Be(42);
    }
}
