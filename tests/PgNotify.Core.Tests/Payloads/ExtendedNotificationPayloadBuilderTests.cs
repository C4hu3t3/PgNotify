using PgNotify.Payloads;

namespace PgNotify.Core.Tests.Payloads;

public class ExtendedNotificationPayloadBuilderTests
{
    [Fact]
    public void BuildFields_emits_full_field_set_in_order()
    {
        var context = new NotificationPayloadBuilderContext("User", "public", "users", ["Id"], ["Name", "Email"], []);

        var fields = ExtendedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Select(f => f.JsonKey).Should().Equal("entity", "schema", "table", "operation", "keys", "changed", "timestamp");
        fields.Select(f => f.Kind).Should().Equal(
            NotificationPayloadFieldKind.Constant,
            NotificationPayloadFieldKind.Constant,
            NotificationPayloadFieldKind.Constant,
            NotificationPayloadFieldKind.Operation,
            NotificationPayloadFieldKind.Keys,
            NotificationPayloadFieldKind.Changed,
            NotificationPayloadFieldKind.Timestamp);
    }

    [Fact]
    public void BuildFields_uses_empty_string_for_default_schema()
    {
        var context = new NotificationPayloadBuilderContext("User", null, "users", ["Id"], [], []);

        var fields = ExtendedNotificationPayloadBuilder.Instance.BuildFields(context);

        fields.Single(f => f.JsonKey == "schema").ConstantValue.Should().Be(string.Empty);
    }
}
