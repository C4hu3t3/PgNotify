using Microsoft.EntityFrameworkCore;
using PgNotify.Migrations.Tests.TestModels;
using PgNotify.Payloads;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Covers what the <c>Notifications:Fingerprint</c> annotation must react to. The migrations SQL
/// generator skips regeneration whenever the fingerprint is unchanged, so a change the fingerprint
/// cannot see is a trigger function that silently stays stale in every deployed database.
/// </summary>
public class NotificationRegenerationTests
{
    [Fact]
    public void Adding_a_mapped_column_regenerates_an_unfiltered_update_trigger()
    {
        // An unfiltered OnUpdate() watches every mapped column, so the trigger's guard and its
        // "changed" array both have to grow with the table. Before the fingerprint was derived
        // from the generated SQL this diff emitted the ALTER TABLE alone, leaving a function that
        // never fired for updates touching only the new column.
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate()),
            after: mb => mb.Entity<Product>(b =>
            {
                b.Property<string>("Extra");
                b.HasDatabaseNotifications(o => o.OnUpdate());
            })));

        sql.Should().Contain("ADD \"Extra\" text");
        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().Contain("NEW.\"Extra\" IS DISTINCT FROM OLD.\"Extra\"");
    }

    [Fact]
    public void Adding_an_unwatched_column_leaves_an_explicitly_filtered_trigger_alone()
    {
        // The other direction: with an explicit watch list the generated SQL does not depend on
        // the new column, so the fingerprint must not move either. Regenerating here would be
        // harmless but would put trigger DDL in every unrelated migration.
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)),
            after: mb => mb.Entity<Product>(b =>
            {
                b.Property<string>("Extra");
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Name));
            })));

        sql.Should().Contain("ADD \"Extra\" text");
        sql.Should().NotContain("CREATE OR REPLACE FUNCTION");
    }

    [Fact]
    public void An_unchanged_model_produces_no_diff_at_all()
    {
        // Guards the determinism the fingerprint depends on: hashing generated SQL is only a
        // usable diff signal if identical models hash identically across runs.
        static void Model(ModelBuilder mb) =>
            mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.OnUpdate();
                o.WithPayload(NotificationPayloadKind.Minimal);
            });

        MigrationTestHelper.GenerateDiffSql(before: Model, after: Model).Should().BeEmpty();
    }

    [Fact]
    public void Switching_to_an_unconditional_update_regenerates_the_trigger()
    {
        // No column, key or constraint changes here either - going from "compare every column" to
        // "no comparison at all" only changes what the trigger body's UPDATE branch contains,
        // which only the fingerprint can see.
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate()),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o => o.OnUpdate(false))));

        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().NotContain("IS DISTINCT FROM");
    }

    [Fact]
    public void Changing_only_the_payload_shape_regenerates_the_trigger()
    {
        // No column, key or constraint changes here - the fingerprint is the only thing that can
        // carry this difference into the diff.
        var sql = string.Join("\n", MigrationTestHelper.GenerateDiffSql(
            before: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Extended);
            }),
            after: mb => mb.Entity<Product>().HasDatabaseNotifications(o =>
            {
                o.OnInsert();
                o.WithPayload(NotificationPayloadKind.Minimal);
            })));

        sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        sql.Should().Contain("'id'");
    }
}
