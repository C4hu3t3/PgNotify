using PgNotify;
using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify.EFCore.Tests.TestModels;

public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public List<Order> Orders { get; set; } = [];
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}

public class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public string Sku { get; set; } = "";
}

[NotifyChanges]
public class AttributeUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

[NotifyChanges(NotificationOperations.Insert)]
public class AttributeInsertOnlyEntity
{
    public int Id { get; set; }
}

[NotifyChanges(NamePrefix = "attr_")]
public class AttributePrefixedEntity
{
    public int Id { get; set; }
}

[NotifyChanges(NotificationOperations.None)]
public class AttributeNoOperationsEntity
{
    public int Id { get; set; }
}

/// <summary>
/// Every fluent option that has an attribute form, set at once — the attribute half of the parity
/// check. Its fluent twin is <see cref="FluentEquivalentOfAttributeEntity"/>, and the two must
/// produce byte-identical annotations.
/// </summary>
[NotifyChanges(
    NotificationOperations.Update,
    WatchedProperties = [nameof(Name)],
    Payload = NotificationPayloadKind.Extended,
    ChannelStrategy = NotificationChannelStrategy.Topic,
    ChannelArgument = ":",
    ChannelName = "explicit_channel",
    NamePrefix = "attr_",
    PayloadOverflow = NotificationPayloadOverflow.Fail)]
public class AttributeFullyConfiguredEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>The fluent twin of <see cref="AttributeFullyConfiguredEntity"/>, configured in the test itself.</summary>
public class FluentEquivalentOfAttributeEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

[NotifyChanges(PayloadProperties = [nameof(Status)])]
public class AttributeProjectedEntity
{
    public int Id { get; set; }

    public string Status { get; set; } = "";
}

[NotifyChanges(PayloadBuilder = typeof(TestPayloadBuilder))]
public class AttributeCustomPayloadEntity
{
    public int Id { get; set; }
}

[NotifyChanges(ChannelStrategyType = typeof(TestChannelStrategy))]
public class AttributeCustomStrategyEntity
{
    public int Id { get; set; }
}

[NotifyChanges(WatchedProperties = ["NoSuchProperty"])]
public class AttributeUnknownWatchedPropertyEntity
{
    public int Id { get; set; }
}

[NotifyChanges(PayloadBuilder = typeof(TestPayloadBuilder), PayloadProperties = [nameof(Id)])]
public class AttributeConflictingPayloadEntity
{
    public int Id { get; set; }
}

[NotifyChanges(ChannelStrategy = NotificationChannelStrategy.Topic, ChannelStrategyType = typeof(TestChannelStrategy))]
public class AttributeConflictingStrategyEntity
{
    public int Id { get; set; }
}

[NotifyChanges(PayloadBuilder = typeof(string))]
public class AttributeWrongPayloadBuilderTypeEntity
{
    public int Id { get; set; }
}

public sealed class TestChannelStrategy : INotificationChannelNamingStrategy
{
    public string GetChannelName(NotificationChannelNamingContext context) => "from_custom_strategy";
}

public sealed class TestPayloadBuilder : INotificationPayloadBuilder
{
    public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context) =>
        [NotificationPayloadField.Constant("entity", "custom")];
}
