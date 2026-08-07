using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Payloads;
using PgNotify.EFCore.Tests.TestModels;

namespace PgNotify.EFCore.Tests;

public class NotifyChangesAttributeConventionTests
{
    [Fact]
    public void NotifyChanges_attribute_alone_enables_notifications_with_default_operations()
    {
        var model = ConventionDbContext.BuildModel();

        var entityType = model.FindEntityType(typeof(AttributeUser))!;
        var config = entityType.GetNotificationConfiguration();

        config.Should().NotBeNull();
        config!.Operations.Should().Be(NotificationOperations.All);
    }

    [Fact]
    public void NotifyChanges_attribute_with_explicit_operations_is_respected()
    {
        var model = ConventionDbContext.BuildModel();

        var config = model.FindEntityType(typeof(AttributeInsertOnlyEntity))!.GetNotificationConfiguration()!;

        config.Operations.Should().Be(NotificationOperations.Insert);
    }

    [Fact]
    public void NotifyChanges_attribute_defaults_to_the_minimal_payload()
    {
        var model = ConventionDbContext.BuildModel();

        var config = model.FindEntityType(typeof(AttributeUser))!.GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<MinimalNotificationPayloadBuilder>();
    }

    [Fact]
    public void NotifyChanges_attribute_NamePrefix_is_applied()
    {
        var model = ConventionDbContext.BuildModel();

        var config = model.FindEntityType(typeof(AttributePrefixedEntity))!.GetNotificationConfiguration()!;

        config.NamePrefix.Should().Be("attr_");
    }

    [Fact]
    public void Entity_without_the_attribute_is_not_enabled()
    {
        var model = ConventionDbContext.BuildModel();

        model.FindEntityType(typeof(Order))?.IsNotificationsEnabled().Should().NotBe(true);
    }

    [Fact]
    public void Explicit_fluent_configuration_overrides_the_attribute_default()
    {
        var model = ConventionDbContext.BuildModel(mb =>
            mb.Entity<AttributeUser>().HasDatabaseNotifications(o => o.OnDelete()));

        var config = model.FindEntityType(typeof(AttributeUser))!.GetNotificationConfiguration()!;

        config.Operations.Should().Be(NotificationOperations.Delete);
    }
}
