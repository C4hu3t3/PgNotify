using Microsoft.EntityFrameworkCore;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Covers the two ways an entity can share physical storage without being caught by
/// <c>NotificationInheritanceTests</c>' table-per-hierarchy checks: table splitting (two unrelated
/// entity types mapped to the same table via a 1:1 relationship) and being mapped only to a view.
/// </summary>
/// <remarks>
/// Table splitting is measured to be strictly worse than the table-per-hierarchy case: every
/// provider entry point resolves a table's configuration through <c>table.EntityTypeMappings.First()</c>,
/// whose ordering is an EF Core implementation detail that here happens to prefer the table-split
/// "principal" side (see <see cref="Configuring_only_the_principal_side_of_a_table_split_is_still_refused"/>),
/// so a configuration on the "dependent" side alone produced no trigger and no error at all, before
/// this validation existed.
/// </remarks>
public class NotificationTableSharingTests
{
    private sealed class OrderHeader
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
        public OrderDetail Detail { get; set; } = null!;
    }

    private sealed class OrderDetail
    {
        public int OrderHeaderId { get; set; }
        public string ShippingAddress { get; set; } = "";
        public OrderHeader Header { get; set; } = null!;
    }

    private static void ConfigureSplitOrder(ModelBuilder b, bool configureHeader, bool configureDetail)
    {
        b.Entity<OrderHeader>(e =>
        {
            e.ToTable("Orders");
            e.HasOne(x => x.Detail).WithOne(x => x.Header).HasForeignKey<OrderDetail>(x => x.OrderHeaderId);
            if (configureHeader)
            {
                e.HasDatabaseNotifications(o => o.OnInsert().OnUpdate(x => x.Customer));
            }
        });
        b.Entity<OrderDetail>(e =>
        {
            e.ToTable("Orders");
            e.HasKey(x => x.OrderHeaderId);
            if (configureDetail)
            {
                e.HasDatabaseNotifications(o => o.OnInsert().OnUpdate(x => x.ShippingAddress));
            }
        });
    }

    [Fact]
    public void Configuring_both_sides_of_a_table_split_is_refused()
    {
        var act = () => MigrationTestHelper.GenerateCreateSql(b => ConfigureSplitOrder(b, configureHeader: true, configureDetail: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Orders*table splitting*");
    }

    [Fact]
    public void Configuring_only_the_dependent_side_of_a_table_split_is_refused()
    {
        // Before this validation existed, this configuration produced no trigger and no error at
        // all: table.EntityTypeMappings.First() always resolved to OrderHeader (the principal
        // side), so OrderDetail's own configuration was invisible.
        var act = () => MigrationTestHelper.GenerateCreateSql(b => ConfigureSplitOrder(b, configureHeader: false, configureDetail: true));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Orders*table splitting*");
    }

    [Fact]
    public void Configuring_only_the_principal_side_of_a_table_split_is_still_refused()
    {
        // This one used to "work" - table.EntityTypeMappings.First() happens to resolve to
        // OrderHeader - but only by an EF Core implementation detail neither entity's own
        // configuration controls, so it is refused for consistency with the other two cases.
        var act = () => MigrationTestHelper.GenerateCreateSql(b => ConfigureSplitOrder(b, configureHeader: true, configureDetail: false));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Orders*table splitting*");
    }

    private sealed class ViewOnlyThing
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void Configuring_notifications_on_a_view_only_entity_is_refused()
    {
        // Before this validation existed, this configuration produced no trigger and no error at
        // all: entityType.GetTableName() is null for a view-only mapping, so
        // GetNotificationConfiguration() silently returned null.
        var act = () => MigrationTestHelper.GenerateCreateSql(b =>
            b.Entity<ViewOnlyThing>(e =>
            {
                e.ToView("ThingsView");
                e.HasDatabaseNotifications(o => o.OnInsert());
            }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ViewOnlyThing*ThingsView*view*");
    }

    [Fact]
    public void Configuring_notifications_on_an_entity_mapped_to_both_a_table_and_a_view_is_allowed()
    {
        // A common pattern: query through the view, write through the table. The trigger belongs
        // on the table regardless - the view mapping does not interfere.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(b =>
            b.Entity<ViewOnlyThing>(e =>
            {
                e.ToTable("things");
                e.ToView("things_view");
                e.HasDatabaseNotifications(o => o.OnInsert());
            })));

        sql.Should().Contain("CREATE TRIGGER trg_things_notify");
    }

    [Fact]
    public void Two_entities_on_different_tables_can_each_have_notifications()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(b =>
        {
            b.Entity<Product>().HasDatabaseNotifications(o => o.OnInsert());
            b.Entity<Invoice>().HasDatabaseNotifications(o => o.OnInsert());
        }));

        sql.Should().Contain("CREATE TRIGGER \"trg_Product_notify\"");
        sql.Should().Contain("CREATE TRIGGER \"trg_Invoice_notify\"");
    }
}
