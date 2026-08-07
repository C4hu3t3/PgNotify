using PgNotify;
using PgNotify.Naming;

namespace PgNotify.Core.Tests.Naming;

public class PerEntityChannelNamingStrategyTests
{
    [Theory]
    [InlineData(NotificationOperation.Insert)]
    [InlineData(NotificationOperation.Update)]
    [InlineData(NotificationOperation.Delete)]
    public void GetChannelName_returns_table_name_regardless_of_operation(NotificationOperation operation)
    {
        var strategy = PerEntityChannelNamingStrategy.Instance;
        var context = new NotificationChannelNamingContext("User", "public", "users", operation);

        strategy.GetChannelName(context).Should().Be("users");
    }

    [Fact]
    public void GetChannelName_truncates_and_hashes_overlong_table_names()
    {
        var longTableName = new string('a', 100);
        var strategy = PerEntityChannelNamingStrategy.Instance;
        var context = new NotificationChannelNamingContext("Entity", null, longTableName, NotificationOperation.Insert);

        var channelName = strategy.GetChannelName(context);

        channelName.Should().HaveLength(63);
        channelName.Should().StartWith(new string('a', 54));
        channelName.Should().MatchRegex("_[0-9a-f]{8}$");
    }

    [Fact]
    public void GetChannelName_is_deterministic_for_overlong_names()
    {
        var longTableName = new string('b', 200);
        var strategy = PerEntityChannelNamingStrategy.Instance;
        var context = new NotificationChannelNamingContext("Entity", null, longTableName, NotificationOperation.Insert);

        strategy.GetChannelName(context).Should().Be(strategy.GetChannelName(context));
    }
}
