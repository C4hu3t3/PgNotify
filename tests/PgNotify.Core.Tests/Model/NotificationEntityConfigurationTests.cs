using PgNotify;
using PgNotify.Model;
using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify.Core.Tests.Model;

public class NotificationEntityConfigurationTests
{
    private static NotificationEntityConfiguration CreateConfiguration(
        INotificationChannelNamingStrategy? strategy = null, string? channelNameOverride = null) => new()
    {
        EntityDisplayName = "User",
        Schema = "public",
        TableName = "users",
        Operations = NotificationOperations.All,
        KeyColumns = ["Id"],
        ChannelStrategy = strategy ?? PerEntityChannelNamingStrategy.Instance,
        ChannelNameOverride = channelNameOverride,
        PayloadBuilder = MinimalNotificationPayloadBuilder.Instance,
    };

    [Fact]
    public void GetChannelName_delegates_to_strategy_when_no_override()
    {
        var configuration = CreateConfiguration(TopicChannelNamingStrategy.Instance);

        configuration.GetChannelName(NotificationOperation.Insert).Should().Be("user.created");
    }

    [Fact]
    public void GetChannelName_prefers_explicit_override()
    {
        var configuration = CreateConfiguration(TopicChannelNamingStrategy.Instance, channelNameOverride: "custom_channel");

        configuration.GetChannelName(NotificationOperation.Insert).Should().Be("custom_channel");
        configuration.GetChannelName(NotificationOperation.Delete).Should().Be("custom_channel");
    }

    [Fact]
    public void BuildPayloadFields_delegates_to_payload_builder_with_entity_context()
    {
        var configuration = CreateConfiguration();

        var fields = configuration.BuildPayloadFields();

        fields.Should().Contain(f => f.JsonKey == "entity" && f.ConstantValue == "User");
        fields.Should().Contain(f => f.JsonKey == "id" && f.ColumnName == "Id");
    }
}
