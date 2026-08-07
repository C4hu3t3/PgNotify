using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Covers <c>WithPayload(x =&gt; new { ... })</c>, whose point is that the payload's JSON shape is
/// stated rather than inherited from whichever default the configuration style picked.
/// </summary>
public class PayloadProjectionTests
{
    [Fact]
    public void Selected_properties_become_payload_columns()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => new { x.Name, x.Price });
            })));

        sql.Should().Contain("'Name', NEW.\"Name\"");
        sql.Should().Contain("'Price', NEW.\"Price\"");
        sql.Should().NotContain("'Sku'");
        sql.Should().NotContain("'timestamp'", "a projection emits what was asked for and nothing else");
    }

    [Fact]
    public void The_key_is_projected_even_when_the_selector_omits_it()
    {
        // A payload that cannot identify its row is worse than no payload, and the selector is
        // written by someone thinking about contents, not routing.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => x.Name);
            })));

        sql.Should().Contain("'id', NEW.\"Id\"");
    }

    [Fact]
    public void A_selected_key_is_not_emitted_twice()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => new { x.Id, x.Name });
            })));

        // The first assignment is the full payload; the second is the reduced overflow fallback.
        var payloadLine = sql.Split('\n').First(l => l.Contains("payload := "));
        payloadLine.Split("NEW.\"Id\"").Should().HaveCount(2, "the key belongs in the payload exactly once");
    }

    [Fact]
    public void A_renamed_column_keeps_the_property_name_as_the_payload_key()
    {
        // The trigger reads the renamed column, but the JSON key stays the property name - the
        // payload is deserialized into a .NET event type, which knows nothing about column names.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>(b =>
            {
                b.Property(x => x.Name).HasColumnName("product_name");
                b.HasDatabaseNotifications(o =>
                {
                    o.OnInsert();
                    o.WithPayload(x => x.Name);
                });
            })));

        // Npgsql's SQL generation helper leaves an already-lower-case identifier unquoted.
        sql.Should().Contain("'Name', NEW.product_name");
        sql.Should().NotContain("'product_name',");
    }

    [Fact]
    public void A_projection_reads_OLD_on_delete()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnDelete();
                o.WithPayload(x => x.Name);
            })));

        sql.Should().Contain("'Name', OLD.\"Name\"");
        sql.Should().Contain("'id', OLD.\"Id\"");
    }

    [Fact]
    public void A_projection_is_guarded_against_overflow()
    {
        // The shape most able to overflow, since it is the one that carries row values.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => x.Name);
            })));

        sql.Should().Contain("IF octet_length(payload) > 7999 THEN");
        sql.Should().Contain("'truncated', true");
    }

    [Fact]
    public void Changing_the_projection_regenerates_the_trigger()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => x.Name);
            }),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(x => new { x.Name, x.Sku });
            })));

        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().Contain("'Sku', NEW.\"Sku\"");
    }

    [Fact]
    public void A_composite_key_projection_emits_a_keys_object()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<OrderLine>(b =>
            {
                b.HasKey(x => new { x.OrderId, x.LineNumber });
                b.HasDatabaseNotifications(o =>
                {
                    o.OnInsert();
                    o.WithPayload(x => x.Description);
                });
            })));

        sql.Should().Contain("'keys', json_build_object('OrderId', NEW.\"OrderId\", 'LineNumber', NEW.\"LineNumber\")");
        sql.Should().Contain("'Description', NEW.\"Description\"");
    }
}
