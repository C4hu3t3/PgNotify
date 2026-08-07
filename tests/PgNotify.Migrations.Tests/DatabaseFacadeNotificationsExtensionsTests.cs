using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

public class DatabaseFacadeNotificationsExtensionsTests
{
    [Fact]
    public void GenerateNotificationTriggersScript_produces_the_same_ddl_shape_as_a_migration()
    {
        using var context = MigrationTestHelper.CreateContext(mb =>
            mb.Entity<Product>().HasDatabaseNotifications());

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().Contain("CREATE OR REPLACE FUNCTION");
        script.Should().Contain("\"fn_Product_notify\"");
        script.Should().Contain("DROP TRIGGER IF EXISTS \"trg_Product_notify\" ON \"Product\"");
        script.Should().Contain("CREATE TRIGGER \"trg_Product_notify\"");
        script.Should().Contain("pg_notify('Product'");
    }

    [Fact]
    public void GenerateNotificationTriggersScript_includes_a_comment_header_per_entity()
    {
        using var context = MigrationTestHelper.CreateContext(mb =>
            mb.Entity<Product>().HasDatabaseNotifications());

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().Contain("-- Product (Product)");
    }

    [Fact]
    public void GenerateNotificationTriggersScript_covers_every_configured_entity()
    {
        using var context = MigrationTestHelper.CreateContext(mb =>
        {
            mb.Entity<Product>().HasDatabaseNotifications();
            mb.Entity<Invoice>().HasDatabaseNotifications();
        });

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().Contain("\"fn_Product_notify\"");
        script.Should().Contain("\"fn_Invoice_notify\"");
    }

    [Fact]
    public void GenerateNotificationTriggersScript_skips_entities_without_notifications()
    {
        using var context = MigrationTestHelper.CreateContext(mb =>
        {
            mb.Entity<Product>().HasDatabaseNotifications();
            mb.Entity<Invoice>();
        });

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().Contain("Product");
        script.Should().NotContain("Invoice");
    }

    [Fact]
    public void GenerateNotificationTriggersScript_respects_watched_columns()
    {
        using var context = MigrationTestHelper.CreateContext(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)));

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().Contain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
        script.Should().NotContain("NEW.\"Sku\" IS DISTINCT FROM OLD.\"Sku\"");
    }

    [Fact]
    public void GenerateNotificationTriggersScript_is_empty_when_nothing_is_configured()
    {
        using var context = MigrationTestHelper.CreateContext(mb => mb.Entity<Product>());

        var script = context.Database.GenerateNotificationTriggersScript();

        script.Should().BeEmpty();
    }
}
