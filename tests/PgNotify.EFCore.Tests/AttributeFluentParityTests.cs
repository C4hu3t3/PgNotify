using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PgNotify;
using PgNotify.Payloads;
using PgNotify.EFCore.Tests.TestModels;

namespace PgNotify.EFCore.Tests;

/// <summary>
/// Everything configurable fluently is configurable by attribute, and the two write the same
/// annotations — so moving an entity between the styles changes nothing about what is generated or
/// what the payload looks like. Asserting on the annotations rather than on behavior is deliberate:
/// they are the single thing both styles produce, and the SQL generator, the fingerprint and the
/// listener all read them.
/// </summary>
public class AttributeFluentParityTests
{
    private static IReadOnlyDictionary<string, object?> NotificationAnnotations(IEntityType entityType) =>
        entityType.GetAnnotations()
            .Where(a => a.Name.StartsWith("Notifications:", StringComparison.Ordinal))
            .ToDictionary(a => a.Name, a => a.Value);

    [Fact]
    public void The_attribute_and_the_fluent_API_write_identical_annotations()
    {
        var model = ConventionDbContext.BuildModel(mb =>
        {
            mb.Entity<AttributeFullyConfiguredEntity>();
            mb.Entity<FluentEquivalentOfAttributeEntity>().HasDatabaseNotifications(o => o
                .OnUpdate(x => x.Name)
                .WithPayload(NotificationPayloadKind.Extended)
                .WithTopicChannel(":")
                .WithChannelName("explicit_channel")
                .WithNamePrefix("attr_")
                .WithPayloadOverflow(NotificationPayloadOverflow.Fail));
        });

        var fromAttribute = NotificationAnnotations(model.FindEntityType(typeof(AttributeFullyConfiguredEntity))!);
        var fromFluent = NotificationAnnotations(model.FindEntityType(typeof(FluentEquivalentOfAttributeEntity))!);

        fromAttribute.Should().NotBeEmpty();
        fromAttribute.Should().BeEquivalentTo(fromFluent);
    }

    [Fact]
    public void WatchedProperties_resolve_to_the_same_columns_as_OnUpdates_selector()
    {
        var config = ConventionDbContext
            .BuildModel(mb => mb.Entity<AttributeFullyConfiguredEntity>())
            .FindEntityType(typeof(AttributeFullyConfiguredEntity))!
            .GetNotificationConfiguration()!;

        config.Operations.Should().Be(NotificationOperations.Update);
        config.WatchedUpdateColumns.Should().Equal("Name");
    }

    [Fact]
    public void PayloadProperties_select_the_projected_payload_builder()
    {
        var config = ConventionDbContext
            .BuildModel(mb => mb.Entity<AttributeProjectedEntity>())
            .FindEntityType(typeof(AttributeProjectedEntity))!
            .GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<ProjectedNotificationPayloadBuilder>();
        config.PayloadColumns.Select(c => c.PropertyName).Should().Equal("Status");
    }

    [Fact]
    public void PayloadBuilder_selects_a_custom_builder_by_type()
    {
        var config = ConventionDbContext
            .BuildModel(mb => mb.Entity<AttributeCustomPayloadEntity>())
            .FindEntityType(typeof(AttributeCustomPayloadEntity))!
            .GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<TestPayloadBuilder>();
    }

    [Fact]
    public void ChannelStrategyType_selects_a_custom_strategy_by_type()
    {
        var config = ConventionDbContext
            .BuildModel(mb => mb.Entity<AttributeCustomStrategyEntity>())
            .FindEntityType(typeof(AttributeCustomStrategyEntity))!
            .GetNotificationConfiguration()!;

        config.ChannelStrategy.Should().BeOfType<TestChannelStrategy>();
        config.GetChannelName(NotificationOperation.Insert).Should().Be("from_custom_strategy");
    }

    [Fact]
    public void A_misspelled_watched_property_fails_when_the_model_is_built()
    {
        // The attribute forms are strings, so this is what replaces the fluent selectors'
        // refactor-safety: the same validation convention rejects both.
        var act = () => ConventionDbContext.BuildModel(mb => mb.Entity<AttributeUnknownWatchedPropertyEntity>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchProperty*");
    }

    [Fact]
    public void Setting_two_payload_sources_at_once_is_rejected()
    {
        var act = () => ConventionDbContext.BuildModel(mb => mb.Entity<AttributeConflictingPayloadEntity>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*PayloadBuilder and PayloadProperties*");
    }

    [Fact]
    public void Setting_two_channel_strategies_at_once_is_rejected()
    {
        var act = () => ConventionDbContext.BuildModel(mb => mb.Entity<AttributeConflictingStrategyEntity>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*ChannelStrategy and ChannelStrategyType*");
    }

    [Fact]
    public void A_payload_builder_type_that_is_not_a_payload_builder_is_rejected()
    {
        var act = () => ConventionDbContext.BuildModel(mb => mb.Entity<AttributeWrongPayloadBuilderTypeEntity>());

        act.Should().Throw<InvalidOperationException>().WithMessage("*INotificationPayloadBuilder*");
    }

    [Fact]
    public void Both_styles_converge_an_explicit_empty_operation_set_to_All()
    {
        // [NotifyChanges(NotificationOperations.None)] used to persist None untouched, unlike a bare
        // HasDatabaseNotifications() call, which NotificationOptionsBuilder.Save() coerced to All -
        // the two styles disagreeing about identical intent. NotificationConfigurationWriter.Apply
        // now does the coercion once, for both, so this must hold regardless of which style asked
        // for no operations.
        var fromAttribute = ConventionDbContext
            .BuildModel(mb => mb.Entity<AttributeNoOperationsEntity>())
            .FindEntityType(typeof(AttributeNoOperationsEntity))!
            .GetNotificationConfiguration()!;

        var fromFluent = ConventionDbContext
            .BuildModel(mb => mb.Entity<User>().HasDatabaseNotifications())
            .FindEntityType(typeof(User))!
            .GetNotificationConfiguration()!;

        fromAttribute.Operations.Should().Be(NotificationOperations.All);
        fromFluent.Operations.Should().Be(NotificationOperations.All);
    }
}
