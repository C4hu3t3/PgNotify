using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

public class NotificationNamePrefixTests
{
    [Fact]
    public void WithNamePrefix_prepends_the_prefix_to_both_function_and_trigger_names()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("\"myapp_fn_Product_notify\"");
        combined.Should().Contain("\"myapp_trg_Product_notify\"");
        combined.Should().NotContain("\"fn_Product_notify\"");
        combined.Should().NotContain("\"trg_Product_notify\"");
    }

    [Fact]
    public void HasNotificationNamePrefix_applies_a_model_wide_default()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
        {
            mb.HasNotificationNamePrefix("modeldefault_");
            mb.Entity<Product>().HasDatabaseNotifications();
        });

        var combined = string.Join("\n", sql);

        combined.Should().Contain("\"modeldefault_fn_Product_notify\"");
        combined.Should().Contain("\"modeldefault_trg_Product_notify\"");
    }

    [Fact]
    public void Entity_level_prefix_overrides_the_model_wide_default_in_generated_sql()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
        {
            mb.HasNotificationNamePrefix("modeldefault_");
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("entity_"));
        });

        var combined = string.Join("\n", sql);

        combined.Should().Contain("\"entity_fn_Product_notify\"");
        combined.Should().NotContain("modeldefault_");
    }

    [Fact]
    public void Changing_only_the_prefix_regenerates_the_trigger_with_the_new_name()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("old_")),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("new_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("CREATE OR REPLACE FUNCTION");
        combined.Should().Contain("\"new_fn_Product_notify\"");
        combined.Should().Contain("\"new_trg_Product_notify\"");
    }

    [Fact]
    public void Changing_the_prefix_drops_the_old_prefixed_objects_before_creating_the_new_ones()
    {
        // Regression test: the regeneration branch used to resolve only the *new* configuration and
        // never looked at what was deployed under the *old* prefix, so old_trg_Product_notify /
        // old_fn_Product_notify were left attached and firing forever - every write notified twice,
        // permanently, alongside the new_-prefixed pair.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("old_")),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("new_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"old_trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"old_fn_Product_notify\"() CASCADE");

        var oldDropIndex = combined.IndexOf("DROP TRIGGER IF EXISTS \"old_trg_Product_notify\"", StringComparison.Ordinal);
        var newCreateIndex = combined.IndexOf("CREATE TRIGGER \"new_trg_Product_notify\"", StringComparison.Ordinal);
        oldDropIndex.Should().BeLessThan(newCreateIndex);
    }

    [Fact]
    public void Changing_an_unrelated_setting_with_the_same_prefix_does_not_emit_a_spurious_removal()
    {
        // Guards the other direction of the same fix: comparing old and new prefix must be an
        // equality check, not "always drop whatever the old prefix was". BuildUpsertStatements
        // itself always emits one DROP TRIGGER IF EXISTS before CREATE TRIGGER (Postgres cannot
        // replace a trigger in place) - what must NOT happen is a *second*, spurious one from the
        // new removal branch, and BuildUpsertStatements never drops the function at all, so any
        // DROP FUNCTION here can only have come from that branch firing when it shouldn't.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_").OnInsert()),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_").OnInsert().OnUpdate()));

        var combined = string.Join("\n", sql);

        CountOccurrences(combined, "DROP TRIGGER IF EXISTS \"myapp_trg_Product_notify\"").Should().Be(1);
        combined.Should().NotContain("DROP FUNCTION IF EXISTS \"myapp_fn_Product_notify\"");
        combined.Should().Contain("CREATE TRIGGER \"myapp_trg_Product_notify\"");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void Turning_notifications_off_with_a_custom_prefix_drops_the_correctly_prefixed_objects()
    {
        // This is the regression test for removal-time prefix resolution: by the time this
        // AlterTableOperation is generated, the entity's *current* configuration has no
        // notifications at all, so the SQL generator cannot re-resolve NamePrefix from the model
        // — it must come from the OldTable annotation surfaced by
        // NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")),
            after: mb => mb.Entity<Product>());

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"myapp_trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"myapp_fn_Product_notify\"() CASCADE");
        combined.Should().NotContain("DROP TRIGGER IF EXISTS \"trg_Product_notify\"");
        combined.Should().NotContain("CREATE OR REPLACE FUNCTION");
    }

    [Fact]
    public void Dropping_a_table_with_a_custom_prefix_drops_the_correctly_prefixed_objects_before_the_table()
    {
        // Same regression as above, but for the DropTableOperation path (whole entity/table
        // removed, not just notifications turned off) — a second, independent code path in
        // NpgsqlNotificationsMigrationsSqlGenerator that also needs the OldTable-less annotation.
        var sql = MigrationTestHelper.GenerateDropSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"myapp_trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"myapp_fn_Product_notify\"() CASCADE");
        combined.Should().Contain("DROP TABLE \"Product\"");

        var triggerDropIndex = combined.IndexOf("DROP TRIGGER", StringComparison.Ordinal);
        var tableDropIndex = combined.IndexOf("DROP TABLE", StringComparison.Ordinal);
        triggerDropIndex.Should().BeLessThan(tableDropIndex);
    }

    [Fact]
    public void Renaming_a_notified_table_drops_the_old_named_objects_before_the_rename()
    {
        // Regression test: trigger/function names embed the table name
        // (NotificationTriggerSqlBuilder.GetNames), so a rename alone moves them even though the
        // notification configuration itself did not change. The AlterTableOperation the differ also
        // emits for the resulting fingerprint change only ever resolves the *current* config under
        // the *new* table name - it never learns the old name existed - so without a fix on
        // RenameTableOperation itself, the old-named trigger and function were orphaned and kept
        // firing forever alongside the newly-named pair.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")),
            after: mb => mb.Entity<Product>().ToTable("Products2").HasDatabaseNotifications(o => o.WithNamePrefix("myapp_")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"myapp_trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"myapp_fn_Product_notify\"() CASCADE");
        combined.Should().Contain("CREATE TRIGGER \"myapp_trg_Products2_notify\"");
        combined.Should().Contain("\"myapp_fn_Products2_notify\"");

        var oldDropIndex = combined.IndexOf("DROP TRIGGER IF EXISTS \"myapp_trg_Product_notify\"", StringComparison.Ordinal);
        var renameIndex = combined.IndexOf("RENAME TO \"Products2\"", StringComparison.Ordinal);
        var newCreateIndex = combined.IndexOf("CREATE TRIGGER \"myapp_trg_Products2_notify\"", StringComparison.Ordinal);

        oldDropIndex.Should().BeLessThan(renameIndex);
        renameIndex.Should().BeLessThan(newCreateIndex);
    }

    [Fact]
    public void Renaming_a_table_without_notifications_emits_no_notification_sql()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>(),
            after: mb => mb.Entity<Product>().ToTable("Products2"));

        var combined = string.Join("\n", sql);

        combined.Should().NotContain("TRIGGER");
        combined.Should().NotContain("FUNCTION");
    }

    [Fact]
    public void Default_unprefixed_behavior_is_unchanged()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb => mb.Entity<Product>().HasDatabaseNotifications());

        var combined = string.Join("\n", sql);

        combined.Should().Contain("\"fn_Product_notify\"");
        combined.Should().Contain("\"trg_Product_notify\"");
    }
}
