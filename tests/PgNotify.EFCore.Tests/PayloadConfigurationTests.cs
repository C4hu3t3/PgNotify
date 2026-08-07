using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;
using PgNotify.EFCore.Tests.TestModels;

namespace PgNotify.EFCore.Tests;

public class PayloadConfigurationTests
{
    [Fact]
    public void WithPayload_selector_records_the_projected_columns()
    {
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(x => new { x.Name, x.Email })));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.PayloadBuilder.Should().BeOfType<ProjectedNotificationPayloadBuilder>();
        config.PayloadColumns.Should().Equal(
            new NotificationPayloadColumn("Name", "Name"),
            new NotificationPayloadColumn("Email", "Email"));
    }

    [Fact]
    public void WithPayload_selector_keeps_both_the_property_name_and_the_column_name()
    {
        // Both are needed, for different consumers: the trigger reads the column, while the JSON
        // key stays the property name so the event type the payload is deserialized into still
        // binds after a column is renamed.
        using var context = FluentDbContext.Create(mb =>
            mb.Entity<User>(b =>
            {
                b.Property(x => x.Name).HasColumnName("full_name");
                b.HasDatabaseNotifications(o => o.WithPayload(x => x.Name));
            }));

        var config = context.Model.FindEntityType(typeof(User))!.GetNotificationConfiguration()!;

        config.PayloadColumns.Should().Equal(new NotificationPayloadColumn("Name", "full_name"));
    }

    [Fact]
    public void WithPayload_kind_selects_the_built_in_shapes()
    {
        using var minimalContext = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Minimal)));
        using var extendedContext = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Extended)));

        var minimal = minimalContext.Model;
        var extended = extendedContext.Model;

        minimal.FindEntityType(typeof(User))!.GetNotificationConfiguration()!
            .PayloadBuilder.Should().BeOfType<MinimalNotificationPayloadBuilder>();
        extended.FindEntityType(typeof(User))!.GetNotificationConfiguration()!
            .PayloadBuilder.Should().BeOfType<ExtendedNotificationPayloadBuilder>();
    }

    [Fact]
    public void The_obsolete_shorthands_still_resolve_to_the_same_builders()
    {
        // Kept working for callers that have not migrated to WithPayload(NotificationPayloadKind).
#pragma warning disable CS0618 // Type or member is obsolete
        using var minimalContext = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithMinimalPayload()));
        using var extendedContext = FluentDbContext.Create(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithExtendedPayload()));
#pragma warning restore CS0618

        var minimal = minimalContext.Model;
        var extended = extendedContext.Model;

        minimal.FindEntityType(typeof(User))!.GetNotificationConfiguration()!
            .PayloadBuilder.Should().BeOfType<MinimalNotificationPayloadBuilder>();
        extended.FindEntityType(typeof(User))!.GetNotificationConfiguration()!
            .PayloadBuilder.Should().BeOfType<ExtendedNotificationPayloadBuilder>();
    }

    [Fact]
    public void Projecting_a_navigation_property_fails_at_model_build_time()
    {
        // ConventionDbContext, not FluentDbContext: this check lives in the validation convention,
        // which only runs when the convention set plugin is wired up.
        var act = () => ConventionDbContext.BuildModel(mb =>
        {
            mb.Entity<Order>();
            mb.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(x => x.Orders));
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*projects 'Orders'*navigation property*");
    }
}
