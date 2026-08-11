using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Migrations.Tests.TestModels;
using PgNotify.Payloads;

namespace PgNotify.Migrations.Tests;

public class NotificationsSqlGenerationTests
{
    [Fact]
    public void Create_table_with_default_notifications_emits_function_and_trigger()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications());

        var combined = string.Join("\n---\n", sql);

        combined.Should().Contain("CREATE OR REPLACE FUNCTION");
        combined.Should().Contain("\"fn_Product_notify\"");
        combined.Should().Contain("DROP TRIGGER IF EXISTS \"trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("CREATE TRIGGER \"trg_Product_notify\"");
        combined.Should().Contain("AFTER INSERT OR UPDATE OR DELETE ON \"Product\"");
        combined.Should().Contain("FOR EACH ROW");
        combined.Should().Contain("EXECUTE FUNCTION \"fn_Product_notify\"()");
        combined.Should().Contain("pg_notify('Product'");
    }

    [Fact]
    public void Create_table_without_notifications_emits_no_trigger_sql()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb => mb.Entity<Product>());

        string.Join("\n", sql).Should().NotContain("pg_notify");
    }

    [Fact]
    public void Only_configured_operations_appear_in_trigger_definition()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("AFTER INSERT OR DELETE ON");
        combined.Should().NotContain("AFTER INSERT OR UPDATE OR DELETE");
        combined.Should().NotContain("TG_OP = 'UPDATE'");
    }

    [Fact]
    public void Watched_update_columns_generate_is_distinct_from_guard()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
        combined.Should().NotContain("NEW.\"Sku\" IS DISTINCT FROM OLD.\"Sku\"");
    }

    [Fact]
    public void Unfiltered_update_watches_every_mapped_column()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate()));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
        combined.Should().Contain("NEW.\"Sku\" IS DISTINCT FROM OLD.\"Sku\"");
        combined.Should().Contain("NEW.\"Price\" IS DISTINCT FROM OLD.\"Price\"");
    }

    [Fact]
    public void OnUpdate_true_is_identical_to_the_bare_call()
    {
        var explicitTrue = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(true)));
        var bare = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate()));

        explicitTrue.Should().Equal(bare);
    }

    [Fact]
    public void OnUpdate_false_raises_unconditionally_without_comparing_any_column()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(false)));

        var combined = string.Join("\n", sql);

        // No IS DISTINCT FROM guard, and no pointless "IF TRUE THEN" wrapper either - the
        // notification is unconditional, same shape as the INSERT/DELETE branches.
        combined.Should().Contain("IF TG_OP = 'UPDATE' THEN\n        PERFORM pg_notify(");
        combined.Should().NotContain("IS DISTINCT FROM");
        combined.Should().NotContain("IF TRUE THEN");
    }

    [Fact]
    public void OnAny_true_is_identical_to_chaining_all_three_operations_with_OnUpdate_true()
    {
        var viaOnAny = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnAny(true)));
        var viaChain = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate(true);
                o.OnDelete();
            }));

        viaOnAny.Should().Equal(viaChain);
    }

    [Fact]
    public void OnAny_defaults_to_true()
    {
        var viaDefault = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnAny()));
        var viaExplicitTrue = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnAny(true)));

        viaDefault.Should().Equal(viaExplicitTrue);
    }

    [Fact]
    public void OnAny_false_makes_every_operation_unconditional()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnAny(false)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("AFTER INSERT OR UPDATE OR DELETE ON");
        combined.Should().Contain("IF TG_OP = 'UPDATE' THEN\n        PERFORM pg_notify(");
        combined.Should().NotContain("IS DISTINCT FROM");
    }

    [Fact]
    public void OnAny_with_a_selector_watches_only_those_columns_on_the_update_branch()
    {
        var viaOnAny = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.OnAny(x => x.Name)));
        var viaChain = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate(x => x.Name);
                o.OnDelete();
            }));

        viaOnAny.Should().Equal(viaChain);

        var combined = string.Join("\n", viaOnAny);
        combined.Should().Contain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
        combined.Should().NotContain("NEW.\"Sku\" IS DISTINCT FROM OLD.\"Sku\"");
    }

    [Fact]
    public void Minimal_payload_uses_id_field_for_single_column_key()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Minimal)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("'id'");
        combined.Should().Contain("NEW.\"Id\"");
        combined.Should().NotContain("'keys'");
    }

    [Fact]
    public void Composite_key_entity_uses_keys_object_even_for_minimal_payload()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<OrderLine>(b =>
            {
                b.HasKey(x => new { x.OrderId, x.LineNumber });
                b.HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Minimal));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("'keys'");
        combined.Should().Contain("json_build_object('OrderId', NEW.\"OrderId\", 'LineNumber', NEW.\"LineNumber\")");
    }

    [Fact]
    public void Topic_channel_strategy_uses_distinct_channel_per_operation()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithTopicChannel()));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("pg_notify('product.created'");
        combined.Should().Contain("pg_notify('product.updated'");
        combined.Should().Contain("pg_notify('product.deleted'");
    }

    [Fact]
    public void Explicit_channel_name_override_is_used_for_every_operation()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithChannelName("custom_channel")));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("pg_notify('custom_channel'");
        combined.Should().NotContain("pg_notify('Product'");
    }

    [Fact]
    public void Changing_watched_columns_alone_regenerates_trigger_sql()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Sku)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("CREATE OR REPLACE FUNCTION");
        combined.Should().Contain("NEW.\"Sku\" IS DISTINCT FROM OLD.\"Sku\"");
        combined.Should().NotContain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
    }

    [Fact]
    public void Unrelated_table_alteration_does_not_regenerate_unchanged_trigger_sql()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>(b =>
            {
                b.HasDatabaseNotifications();
                b.ToTable(t => t.HasComment(null));
            }),
            after: mb => mb.Entity<Product>(b =>
            {
                b.HasDatabaseNotifications();
                b.ToTable(t => t.HasComment("a product"));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("COMMENT ON TABLE");
        combined.Should().NotContain("pg_notify");
        combined.Should().NotContain("CREATE OR REPLACE FUNCTION");
    }

    [Fact]
    public void Removing_notifications_from_an_existing_table_drops_trigger_and_function()
    {
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(),
            after: mb => mb.Entity<Product>());

        var combined = string.Join("\n", sql);

        combined.Should().Contain("DROP TRIGGER IF EXISTS \"trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"fn_Product_notify\"() CASCADE");
        combined.Should().NotContain("CREATE OR REPLACE FUNCTION");
    }

    [Fact]
    public void Dropping_a_table_with_notifications_drops_trigger_and_function_before_the_table()
    {
        var sql = MigrationTestHelper.GenerateDropSql(mb => mb.Entity<Product>().HasDatabaseNotifications());

        var combined = string.Join("\n", sql);
        combined.Should().Contain("DROP TRIGGER IF EXISTS \"trg_Product_notify\" ON \"Product\"");
        combined.Should().Contain("DROP FUNCTION IF EXISTS \"fn_Product_notify\"() CASCADE");
        combined.Should().Contain("DROP TABLE \"Product\"");

        var triggerDropIndex = combined.IndexOf("DROP TRIGGER", StringComparison.Ordinal);
        var tableDropIndex = combined.IndexOf("DROP TABLE", StringComparison.Ordinal);
        triggerDropIndex.Should().BeLessThan(tableDropIndex);
    }

    [Fact]
    public void Dropping_a_table_without_notifications_emits_no_trigger_cleanup()
    {
        var sql = MigrationTestHelper.GenerateDropSql(mb => mb.Entity<Product>());

        string.Join("\n", sql).Should().NotContain("pg_notify").And.NotContain("DROP FUNCTION");
    }

    [Fact]
    public void Schema_qualified_table_delimits_schema_in_generated_sql()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Invoice>(b =>
            {
                b.ToTable("Invoice", "billing");
                b.HasDatabaseNotifications();
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("billing.\"Invoice\"");
        combined.Should().Contain("billing.\"fn_billing_Invoice_notify\"");
    }

    [Fact]
    public void Watched_json_column_is_cast_to_jsonb_before_comparison()
    {
        // json has no comparison operator at all - PostgreSQL raises "operator does not exist:
        // json = json" at runtime, on the first UPDATE that hits the guard (CREATE FUNCTION itself
        // succeeds; PL/pgSQL only type-checks an expression the first time it executes). Confirmed
        // against a real PostgreSQL 16 instance. jsonb also has the side benefit of comparing by
        // parsed value, so key order does not cause a spurious "changed".
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Document>(b =>
            {
                b.Property(x => x.Metadata).HasColumnType("json");
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Metadata));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("NEW.\"Metadata\"::jsonb IS DISTINCT FROM OLD.\"Metadata\"::jsonb");
    }

    [Fact]
    public void Watched_xml_column_is_cast_to_text_before_comparison()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Document>(b =>
            {
                b.Property(x => x.Notes).HasColumnType("xml");
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Notes));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("NEW.\"Notes\"::text IS DISTINCT FROM OLD.\"Notes\"::text");
    }

    [Fact]
    public void Watched_ordinary_column_is_compared_without_a_cast()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Document>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("NEW.\"Name\" IS DISTINCT FROM OLD.\"Name\"");
        combined.Should().NotContain("NEW.\"Name\"::");
    }

    [Fact]
    public void Changed_field_of_the_extended_payload_also_casts_a_json_column()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Document>(b =>
            {
                b.Property(x => x.Metadata).HasColumnType("json");
                b.HasDatabaseNotifications(o =>
                {
                    o.OnUpdate(x => x.Metadata);
                    o.WithPayload(NotificationPayloadKind.Extended);
                });
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("'Metadata', NEW.\"Metadata\"::jsonb IS DISTINCT FROM OLD.\"Metadata\"::jsonb");
    }

    [Fact]
    public void Changing_a_watched_columns_store_type_alone_regenerates_trigger_sql()
    {
        // The fingerprint hashes the generated SQL, and the generated SQL now depends on column
        // store types - so a bare text -> json change on an already-watched column must move the
        // fingerprint and regenerate the trigger function, even though nothing about the
        // notification configuration itself changed.
        var sql = MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Document>(b =>
            {
                b.Property(x => x.Metadata).HasColumnType("text");
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Metadata));
            }),
            after: mb => mb.Entity<Document>(b =>
            {
                b.Property(x => x.Metadata).HasColumnType("json");
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Metadata));
            }));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("CREATE OR REPLACE FUNCTION");
        combined.Should().Contain("NEW.\"Metadata\"::jsonb IS DISTINCT FROM OLD.\"Metadata\"::jsonb");
    }

    [Fact]
    public void Extended_payload_includes_full_field_set()
    {
        var sql = MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o => o.WithPayload(NotificationPayloadKind.Extended)));

        var combined = string.Join("\n", sql);

        combined.Should().Contain("'entity'");
        combined.Should().Contain("'schema'");
        combined.Should().Contain("'table'");
        combined.Should().Contain("'operation'");
        combined.Should().Contain("'keys'");
        combined.Should().Contain("'changed'");
        combined.Should().Contain("'timestamp'");
        combined.Should().Contain("clock_timestamp()");
    }
}
