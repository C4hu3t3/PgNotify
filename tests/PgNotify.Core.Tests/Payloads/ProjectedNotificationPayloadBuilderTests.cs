using PgNotify.Payloads;

namespace PgNotify.Core.Tests.Payloads;

public class ProjectedNotificationPayloadBuilderTests
{
    private static NotificationPayloadBuilderContext Context(
        IReadOnlyList<string> keyColumns,
        params NotificationPayloadColumn[] payloadColumns) =>
        new("Order", "public", "orders", keyColumns, [], payloadColumns);

    [Fact]
    public void Emits_entity_operation_key_and_every_selected_column()
    {
        var context = Context(["Id"], new("Status", "Status"), new("Total", "Total"));

        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Should().HaveCount(5);
        fields[0].Should().Be(NotificationPayloadField.Constant("entity", "Order"));
        fields[1].Should().Be(NotificationPayloadField.Of("operation", NotificationPayloadFieldKind.Operation));
        fields[2].Should().Be(NotificationPayloadField.Column("id", "Id"));
        fields[3].Should().Be(NotificationPayloadField.Column("Status", "Status"));
        fields[4].Should().Be(NotificationPayloadField.Column("Total", "Total"));
    }

    [Fact]
    public void Keys_the_payload_on_the_property_name_not_the_column_name()
    {
        // The payload is deserialized into a .NET event type, so a renamed column - by
        // HasColumnName here, or wholesale by a naming-convention package - must not change the
        // JSON key and silently leave the event's member unbound.
        var context = Context(["Id"], new NotificationPayloadColumn("InternalNote", "internal_note"));

        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Should().Contain(NotificationPayloadField.Column("InternalNote", "internal_note"));
    }

    [Fact]
    public void Emits_the_key_even_when_nothing_is_selected()
    {
        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(Context(["Id"]));

        fields.Should().Contain(NotificationPayloadField.Column("id", "Id"));
    }

    [Fact]
    public void Does_not_emit_a_selected_key_column_twice()
    {
        var context = Context(["Id"], new("Id", "Id"), new("Status", "Status"));

        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Where(f => f.ColumnName == "Id").Should().ContainSingle();
    }

    [Fact]
    public void Recognizes_a_renamed_key_column_as_the_key()
    {
        // The de-duplication has to compare columns, not property names: the key field above was
        // emitted from the key column, which is what a renamed key still resolves to.
        var context = Context(["order_id"], new("OrderId", "order_id"), new("Status", "Status"));

        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Where(f => f.ColumnName == "order_id").Should().ContainSingle();
    }

    [Fact]
    public void Uses_a_keys_object_for_a_composite_key()
    {
        var context = new NotificationPayloadBuilderContext(
            "OrderLine", null, "order_lines", ["OrderId", "LineNumber"], [], [new("Description", "Description")]);

        var fields = ProjectedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Should().ContainSingle(f => f.Kind == NotificationPayloadFieldKind.Keys && f.JsonKey == "keys");
        fields.Should().Contain(NotificationPayloadField.Column("Description", "Description"));
    }
}
