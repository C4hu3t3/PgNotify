using PgNotify.Payloads;

namespace PgNotify.Core.Tests.Payloads;

public class MinimalNotificationPayloadBuilderTests
{
    [Fact]
    public void BuildFields_emits_entity_operation_and_id_for_single_key()
    {
        var context = new NotificationPayloadBuilderContext("User", "public", "users", ["Id"], [], []);

        var fields = MinimalNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Should().HaveCount(3);
        fields[0].Should().Be(NotificationPayloadField.Constant("entity", "User"));
        fields[1].Should().Be(NotificationPayloadField.Of("operation", NotificationPayloadFieldKind.Operation));
        fields[2].Should().Be(NotificationPayloadField.Column("id", "Id"));
    }

    [Fact]
    public void BuildFields_falls_back_to_keys_object_for_composite_keys()
    {
        var context = new NotificationPayloadBuilderContext("OrderItem", null, "order_items", ["OrderId", "LineNumber"], [], []);

        var fields = MinimalNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Should().ContainSingle(f => f.Kind == NotificationPayloadFieldKind.Keys && f.JsonKey == "keys");
        fields.Should().NotContain(f => f.Kind == NotificationPayloadFieldKind.Column);
    }
}
