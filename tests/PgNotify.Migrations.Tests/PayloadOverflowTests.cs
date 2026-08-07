using Microsoft.EntityFrameworkCore;
using PgNotify.Payloads;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Covers the payload overflow guard. Exceeding <c>pg_notify</c>'s 7999-byte limit raises inside
/// the trigger, which aborts the write that produced the row — so what these tests really lock
/// down is that a large row cannot break an INSERT or UPDATE.
/// </summary>
public class PayloadOverflowTests
{
    [Fact]
    public void Extended_payload_falls_back_to_a_reduced_payload_when_oversized()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Extended);
            })));

        sql.Should().Contain("DECLARE");
        sql.Should().Contain("payload text;");
        sql.Should().Contain("IF octet_length(payload) > 7999 THEN");
        sql.Should().Contain("'truncated', true");
        sql.Should().Contain("PERFORM pg_notify('Product', payload);");
    }

    [Fact]
    public void The_reduced_payload_still_identifies_the_row()
    {
        // A notification that cannot say which row changed is worse than useless, so the fallback
        // keeps the key - reading OLD on delete, exactly like the full payload does.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnDelete();
                o.WithPayload(NotificationPayloadKind.Extended);
            })));

        sql.Should().Contain("'id', NEW.\"Id\", 'truncated', true");
        sql.Should().Contain("'id', OLD.\"Id\", 'truncated', true");
    }

    [Fact]
    public void The_minimal_payload_gets_no_guard()
    {
        // It is already the shape the fallback would produce: nothing smaller left to send.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Minimal);
            })));

        sql.Should().NotContain("octet_length");
        sql.Should().NotContain("DECLARE");
    }

    [Fact]
    public void Opting_into_Fail_removes_the_guard()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(mb =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayloadOverflow(NotificationPayloadOverflow.Fail);
            })));

        sql.Should().NotContain("octet_length");
        sql.Should().Contain("PERFORM pg_notify('Product', (json_build_object(");
    }

    [Fact]
    public void Changing_the_overflow_behavior_regenerates_the_trigger()
    {
        // Nothing about the model changes here - only generated SQL does. This is the fingerprint
        // covering a new generation option without that option being registered anywhere.
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Extended);
            }),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Extended);
                o.WithPayloadOverflow(NotificationPayloadOverflow.Fail);
            })));

        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().NotContain("octet_length");
    }
}
