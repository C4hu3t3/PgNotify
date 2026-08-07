using PgNotify;
using PgNotify.Naming;

namespace PgNotify.Core.Tests.Naming;

public class SingleChannelNamingStrategyTests
{
    [Fact]
    public void GetChannelName_uses_default_name_when_not_specified()
    {
        var strategy = new SingleChannelNamingStrategy();
        var context = new NotificationChannelNamingContext("User", "public", "users", NotificationOperation.Insert);

        strategy.GetChannelName(context).Should().Be("entity_changed");
    }

    [Theory]
    [InlineData(NotificationOperation.Insert, "users")]
    [InlineData(NotificationOperation.Update, "orders")]
    [InlineData(NotificationOperation.Delete, "products")]
    public void GetChannelName_ignores_entity_and_operation(NotificationOperation operation, string table)
    {
        var strategy = new SingleChannelNamingStrategy("all_changes");
        var context = new NotificationChannelNamingContext("Whatever", null, table, operation);

        strategy.GetChannelName(context).Should().Be("all_changes");
    }

    [Fact]
    public void Constructor_rejects_blank_channel_name()
    {
        var act = () => new SingleChannelNamingStrategy("   ");
        act.Should().Throw<ArgumentException>();
    }
}
