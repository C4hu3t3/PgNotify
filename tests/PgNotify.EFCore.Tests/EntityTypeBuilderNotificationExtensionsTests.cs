using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Naming;
using PgNotify.Payloads;
using PgNotify.EFCore.Tests.TestModels;

namespace PgNotify.EFCore.Tests;

public class EntityTypeBuilderNotificationExtensionsTests
{
    [Fact]
    public void HasDatabaseNotifications_with_no_configuration_defaults_to_all_operations_per_entity_minimal_payload()
    {
        using var context = FluentDbContext.Create(mb => mb.Entity<User>().HasDatabaseNotifications());

        var entityType = context.Model.FindEntityType(typeof(User))!;
        var config = entityType.GetNotificationConfiguration();

        config.Should().NotBeNull();
        config!.Operations.Should().Be(NotificationOperations.All);
        config.ChannelStrategy.Should().BeOfType<PerEntityChannelNamingStrategy>();
        config.PayloadBuilder.Should().BeOfType<MinimalNotificationPayloadBuilder>(
            "the minimal payload is the default for both configuration styles: an entity that moves "
            + "between them must not silently change payload shape");
        config.ChannelNameOverride.Should().BeNull();
        config.KeyColumns.Should().Equal("Id");
        config.NamePrefix.Should().Be("");
    }

    [Fact]
    public void HasDatabaseNotifications_respects_explicit_operation_selection()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
            }));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.Operations.Should().Be(NotificationOperations.Insert | NotificationOperations.Delete);
    }

    [Fact]
    public void OnUpdate_with_single_property_selector_records_watched_column()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.WatchedUpdateColumns.Should().Equal("Name");
    }

    [Fact]
    public void OnUpdate_with_anonymous_type_selector_records_all_selected_columns()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.OnUpdate(x => new { x.Name, x.Email })));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.WatchedUpdateColumns.Should().BeEquivalentTo(["Name", "Email"]);
    }

    [Fact]
    public void WithSingleChannel_configures_single_channel_strategy()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithSingleChannel("all_changes")));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.ChannelStrategy.Should().BeOfType<SingleChannelNamingStrategy>();
        config.GetChannelName(NotificationOperation.Insert).Should().Be("all_changes");
        config.GetChannelName(NotificationOperation.Delete).Should().Be("all_changes");
    }

    [Fact]
    public void WithTopicChannel_configures_topic_channel_strategy()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithTopicChannel()));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.GetChannelName(NotificationOperation.Insert).Should().Be("user.created");
    }

    [Fact]
    public void WithChannelName_overrides_strategy_for_every_operation()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithTopicChannel().WithChannelName("custom")));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.GetChannelName(NotificationOperation.Insert).Should().Be("custom");
        config.GetChannelName(NotificationOperation.Update).Should().Be("custom");
    }

    [Fact]
    public void WithMinimalPayload_configures_minimal_payload_builder()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Minimal)));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<MinimalNotificationPayloadBuilder>();
    }

    [Fact]
    public void Composite_key_entity_records_all_key_columns_in_order()
    {
        using var context = FluentDbContext.Create(mb =>
        {
            mb.Entity<OrderLine>(b =>
            {
                b.HasKey(x => new { x.OrderId, x.LineNumber });
                b.HasDatabaseNotifications();
            });
        });

        var config = context.Model.FindEntityType(typeof(OrderLine))!.GetNotificationConfiguration()!;

        config.KeyColumns.Should().Equal("OrderId", "LineNumber");
    }

    [Fact]
    public void GetNotificationConfiguration_returns_null_when_not_enabled()
    {
        using var context = FluentDbContext.Create(mb => mb.Entity<User>());

        context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration().Should().BeNull();
        context.Model.FindEntityType(typeof(User))!.IsNotificationsEnabled().Should().BeFalse();
    }

    [Fact]
    public void WithNamePrefix_sets_the_entity_level_prefix()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.NamePrefix.Should().Be("myapp_");
    }

    [Fact]
    public void HasNotificationNamePrefix_sets_a_model_wide_default()
    {
        using var context = FluentDbContext.Create(mb =>
        {
            mb.HasNotificationNamePrefix("modeldefault_");
            mb.Entity<User>().HasDatabaseNotifications();
        });

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.NamePrefix.Should().Be("modeldefault_");
    }

    [Fact]
    public void Entity_level_NamePrefix_overrides_the_model_wide_default()
    {
        using var context = FluentDbContext.Create(mb =>
        {
            mb.HasNotificationNamePrefix("modeldefault_");
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithNamePrefix("entity_"));
        });

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.NamePrefix.Should().Be("entity_");
    }

    [Fact]
    public void EntityDisplayName_comes_from_the_entity_types_metadata_name_not_its_ClrType()
    {
        // Reproduces what EF Core's migrations snapshot reconstruction does when rebuilding the
        // "old" side of a diff: it declares entity types via the string-named
        // ModelBuilder.Entity(string)/SharedTypeEntity overloads, which resolve ClrType to a
        // Dictionary<string, object> placeholder rather than the real POCO. EntityDisplayName
        // must be stable across that — see EntityTypeNotificationExtensions.GetNotificationConfiguration.
        using var context = FluentDbContext.Create(mb =>
            mb.SharedTypeEntity<Dictionary<string, object>>("SharedUser", b =>
            {
                b.IndexerProperty<int>("Id");
                b.HasKey("Id");
                b.HasDatabaseNotifications();
            }));

        var entityType = context.Model.FindEntityType("SharedUser")!;
        entityType.ClrType.Should().Be(typeof(Dictionary<string, object>), "this mirrors what a reconstructed migrations snapshot model looks like");

        var config = entityType.GetNotificationConfiguration()!;

        config.EntityDisplayName.Should().Be("SharedUser");
    }

    private sealed class CustomPayloadBuilder : INotificationPayloadBuilder
    {
        public IReadOnlyList<NotificationPayloadField> BuildFields(NotificationPayloadBuilderContext context) =>
            [NotificationPayloadField.Constant("custom", "value")];
    }

    [Fact]
    public void WithPayload_generic_overload_round_trips_custom_type()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload<CustomPayloadBuilder>()));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<CustomPayloadBuilder>();
        config.BuildPayloadFields().Should().ContainSingle(f => f.JsonKey == "custom");
    }

    private sealed class CustomChannelNamingStrategy : INotificationChannelNamingStrategy
    {
        public string GetChannelName(NotificationChannelNamingContext context) => "custom_strategy_channel";
    }

    [Fact]
    public void WithChannelStrategy_generic_overload_round_trips_custom_type()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithChannelStrategy<CustomChannelNamingStrategy>()));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.ChannelStrategy.Should().BeOfType<CustomChannelNamingStrategy>();
    }
}
