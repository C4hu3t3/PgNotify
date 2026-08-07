using PgNotify;
using PgNotify.Naming;

namespace PgNotify.Core.Tests.Naming;

public class TopicChannelNamingStrategyTests
{
    [Theory]
    [InlineData(NotificationOperation.Insert, "user.created")]
    [InlineData(NotificationOperation.Update, "user.updated")]
    [InlineData(NotificationOperation.Delete, "user.deleted")]
    public void GetChannelName_builds_dot_separated_topic(NotificationOperation operation, string expected)
    {
        var strategy = TopicChannelNamingStrategy.Instance;
        var context = new NotificationChannelNamingContext("User", "public", "users", operation);

        strategy.GetChannelName(context).Should().Be(expected);
    }

    [Fact]
    public void GetChannelName_supports_custom_separator()
    {
        var strategy = new TopicChannelNamingStrategy(separator: "_");
        var context = new NotificationChannelNamingContext("Order", null, "orders", NotificationOperation.Update);

        strategy.GetChannelName(context).Should().Be("order_updated");
    }

    [Fact]
    public void GetChannelName_lowercases_entity_name()
    {
        var strategy = TopicChannelNamingStrategy.Instance;
        var context = new NotificationChannelNamingContext("ShoppingCartItem", null, "shopping_cart_items", NotificationOperation.Delete);

        strategy.GetChannelName(context).Should().Be("shoppingcartitem.deleted");
    }
}
