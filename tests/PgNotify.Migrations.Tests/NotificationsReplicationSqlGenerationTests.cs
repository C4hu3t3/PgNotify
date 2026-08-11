using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

public class NotificationsReplicationSqlGenerationTests
{
    [Fact]
    public void Create_table_with_LogicalReplication_emits_publication_membership_and_slot_but_no_trigger()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication)));

        var combined = string.Join("\n---\n", sql);

        combined.Should().Contain("CREATE PUBLICATION");
        combined.Should().Contain("pgnotify_pub");
        combined.Should().Contain("ALTER PUBLICATION");
        combined.Should().Contain("ADD TABLE");
        combined.Should().Contain("pg_create_logical_replication_slot");
        combined.Should().Contain("pgnotify_default");
        combined.Should().NotContain("CREATE OR REPLACE FUNCTION");
        combined.Should().NotContain("CREATE TRIGGER");
        combined.Should().NotContain("pg_notify");
    }

    [Fact]
    public void Create_table_with_default_Notify_delivery_emits_no_replication_ddl()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb => mb.Entity<Product>().HasDatabaseNotifications());

        var combined = string.Join("\n", sql);

        combined.Should().NotContain("CREATE PUBLICATION");
        combined.Should().NotContain("pg_create_logical_replication_slot");
    }

    [Fact]
    public void WithReplicaIdentityFull_sets_replica_identity_full()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithReplicaIdentityFull()));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("REPLICA IDENTITY FULL");
        combined.Should().NotContain("REPLICA IDENTITY DEFAULT");
    }

    [Fact]
    public void Without_WithReplicaIdentityFull_replica_identity_is_reset_to_default()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("REPLICA IDENTITY DEFAULT");
        combined.Should().NotContain("REPLICA IDENTITY FULL");
    }

    [Fact]
    public void Consumer_group_names_a_distinct_slot()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
                o.WithDelivery(NotificationDeliveryMode.LogicalReplication, "billing")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("pgnotify_billing");
        combined.Should().NotContain("pgnotify_default");
    }

    [Fact]
    public void NamePrefix_scopes_publication_and_slot_names()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithNamePrefix("myapp_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("myapp_pgnotify_pub");
        combined.Should().Contain("myapp_pgnotify_default");
    }

    [Fact]
    public void Removing_LogicalReplication_from_an_existing_table_drops_publication_membership_and_resets_replica_identity()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithReplicaIdentityFull()),
            after: mb => mb.Entity<Product>());

        var combined = string.Join("\n", sql);

        combined.Should().Contain("ALTER PUBLICATION");
        combined.Should().Contain("DROP TABLE");
        combined.Should().Contain("REPLICA IDENTITY DEFAULT");
        // Never touches the slot itself -- see NotificationReplicationSqlBuilder.BuildRemovalStatements.
        combined.Should().NotContain("pg_drop_replication_slot");
        combined.Should().NotContain("DROP PUBLICATION");
    }

    [Fact]
    public void Dropping_a_table_with_LogicalReplication_removes_it_from_the_publication_before_the_table_is_dropped()
    {
        var sql = MigrationTestHelper.GenerateDropSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication)));

        var combined = string.Join("\n", sql);
        combined.Should().Contain("ALTER PUBLICATION");
        combined.Should().Contain("DROP TABLE \"Product\"");

        var publicationDropIndex = combined.IndexOf("ALTER PUBLICATION", StringComparison.Ordinal);
        var tableDropIndex = combined.IndexOf("DROP TABLE \"Product\"", StringComparison.Ordinal);
        publicationDropIndex.Should().BeLessThan(tableDropIndex);
    }

    [Fact]
    public void Switching_from_Notify_to_LogicalReplication_drops_the_trigger_and_adds_publication_ddl()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"fn_Product_notify\"() CASCADE");
        combined.Should().Contain("CREATE PUBLICATION");
        combined.Should().Contain("ALTER PUBLICATION");
    }

    [Fact]
    public void Switching_from_LogicalReplication_to_Notify_removes_publication_membership_and_adds_the_trigger()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication)),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications());

        var combined = string.Join("\n", sql);

        combined.Should().Contain("ALTER PUBLICATION");
        combined.Should().Contain("DROP TABLE");
        combined.Should().Contain("CREATE OR REPLACE FUNCTION");
        combined.Should().Contain("CREATE TRIGGER \"trg_Product_notify\"");
    }

    [Fact]
    public void Renaming_a_LogicalReplication_table_emits_no_trigger_cleanup()
    {
        // Neither the publication name nor the slot name embed the table name, and PostgreSQL
        // tracks publication membership by OID, so a rename needs no notification-specific SQL at
        // all beyond the base ALTER TABLE ... RENAME.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>(b =>
            {
                b.ToTable("Product");
                b.HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication));
            }),
            after: mb => mb.Entity<Product>(b =>
            {
                b.ToTable("Products");
                b.HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("RENAME TO \"Products\"");
        combined.Should().NotContain("DROP TRIGGER");
        combined.Should().NotContain("DROP FUNCTION");
    }

    [Fact]
    public void Changing_NamePrefix_removes_membership_from_the_old_publication_and_adds_it_to_the_new_one()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithNamePrefix("old_")),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o
                .WithDelivery(NotificationDeliveryMode.LogicalReplication)
                .WithNamePrefix("new_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("old_pgnotify_pub");
        combined.Should().Contain("new_pgnotify_pub");

        var oldPubIndex = combined.IndexOf("old_pgnotify_pub", StringComparison.Ordinal);
        var newPubCreateIndex = combined.IndexOf("CREATE PUBLICATION", StringComparison.Ordinal);
        newPubCreateIndex.Should().BeGreaterThan(-1);
        oldPubIndex.Should().BeLessThan(newPubCreateIndex);
    }

    [Fact]
    public void Unrelated_table_alteration_does_not_regenerate_unchanged_replication_ddl()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>(b =>
            {
                b.HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication));
                b.ToTable(t => t.HasComment(null));
            }),
            after: mb => mb.Entity<Product>(b =>
            {
                b.HasDatabaseNotifications(o => o.WithDelivery(NotificationDeliveryMode.LogicalReplication));
                b.ToTable(t => t.HasComment("a product"));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("COMMENT ON TABLE");
        combined.Should().NotContain("CREATE PUBLICATION");
        combined.Should().NotContain("ALTER PUBLICATION");
        combined.Should().NotContain("pg_create_logical_replication_slot");
    }
}
