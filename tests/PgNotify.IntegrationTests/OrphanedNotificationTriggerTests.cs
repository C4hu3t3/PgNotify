using Microsoft.EntityFrameworkCore;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Proves <c>FindOrphanedNotificationTriggersAsync</c> — the database-first counterpart to the
/// migrations path's automatic trigger removal — surfaces exactly the trigger/function pairs this
/// library deployed and the current model no longer configures, without false-positiving on
/// unrelated triggers a database-first schema might also contain.
/// </summary>
public sealed class OrphanedNotificationTriggerTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE test_users (
                "Id" serial PRIMARY KEY,
                "Name" text NOT NULL,
                "Email" text NOT NULL
            );

            -- Stands in for a trigger some other tool (an audit framework, a hand-written
            -- "updated_at" maintainer) put on the same database - not something PgNotify ever
            -- created, and it must never be reported as one of its orphans.
            CREATE FUNCTION unrelated_fn() RETURNS trigger
                LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN NULL;
            END;
            $$;
            COMMENT ON FUNCTION unrelated_fn() IS 'not a pgnotify fingerprint';
            CREATE TRIGGER unrelated_trg AFTER UPDATE ON test_users
                FOR EACH ROW EXECUTE FUNCTION unrelated_fn();
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Nothing_is_orphaned_while_the_entity_is_still_configured()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using var context = new IntegrationDbContext(options);
        await context.Database.EnsureNotificationTriggersAsync();

        var orphans = await context.Database.FindOrphanedNotificationTriggersAsync();

        orphans.Should().BeEmpty();
    }

    [Fact]
    public async Task A_trigger_whose_entity_was_removed_from_the_model_is_reported_as_orphaned()
    {
        using (var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        using var withoutNotifications = new IntegrationDbContextWithoutNotifications(
            new DbContextOptionsBuilder<IntegrationDbContextWithoutNotifications>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);

        var orphans = await withoutNotifications.Database.FindOrphanedNotificationTriggersAsync();

        orphans.Should().ContainSingle();
        var orphan = orphans.Single();
        orphan.Schema.Should().Be("public");
        orphan.TableName.Should().Be("test_users");
        orphan.FunctionName.Should().Be("fn_test_users_notify");
        orphan.TriggerName.Should().Be("trg_test_users_notify");
        orphan.DropStatements.Should().ContainSingle(s => s.Contains("DROP TRIGGER"));
        orphan.DropStatements.Should().ContainSingle(s => s.Contains("DROP FUNCTION"));
    }

    [Fact]
    public async Task Running_the_returned_drop_statements_removes_the_orphaned_trigger_and_function()
    {
        using (var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        using var withoutNotifications = new IntegrationDbContextWithoutNotifications(
            new DbContextOptionsBuilder<IntegrationDbContextWithoutNotifications>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);

        var orphan = (await withoutNotifications.Database.FindOrphanedNotificationTriggersAsync()).Single();
        foreach (var statement in orphan.DropStatements)
        {
            await withoutNotifications.Database.ExecuteSqlRawAsync(statement);
        }

        (await withoutNotifications.Database.FindOrphanedNotificationTriggersAsync()).Should().BeEmpty();
        (await TriggerExistsAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveOrphanedNotificationTriggersAsync_drops_exactly_the_orphaned_trigger_and_function()
    {
        using (var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        using var withoutNotifications = new IntegrationDbContextWithoutNotifications(
            new DbContextOptionsBuilder<IntegrationDbContextWithoutNotifications>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);

        var removed = await withoutNotifications.Database.RemoveOrphanedNotificationTriggersAsync();

        removed.Should().ContainSingle();
        removed.Single().FunctionName.Should().Be("fn_test_users_notify");
        (await TriggerExistsAsync()).Should().BeFalse();
        (await withoutNotifications.Database.FindOrphanedNotificationTriggersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveOrphanedNotificationTriggersAsync_leaves_a_still_configured_entitys_trigger_alone()
    {
        using var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);
        await context.Database.EnsureNotificationTriggersAsync();

        var removed = await context.Database.RemoveOrphanedNotificationTriggersAsync();

        removed.Should().BeEmpty();
        (await TriggerExistsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveOrphanedNotificationTriggersAsync_never_touches_a_trigger_pgnotify_never_created()
    {
        using (var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        using var withoutNotifications = new IntegrationDbContextWithoutNotifications(
            new DbContextOptionsBuilder<IntegrationDbContextWithoutNotifications>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);
        await withoutNotifications.Database.RemoveOrphanedNotificationTriggersAsync();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'unrelated_trg' AND NOT tgisinternal)";
        ((bool)(await command.ExecuteScalarAsync())!).Should().BeTrue();
    }

    [Fact]
    public async Task A_trigger_pgnotify_never_created_is_never_reported_as_orphaned()
    {
        using var withoutNotifications = new IntegrationDbContextWithoutNotifications(
            new DbContextOptionsBuilder<IntegrationDbContextWithoutNotifications>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options);

        var orphans = await withoutNotifications.Database.FindOrphanedNotificationTriggersAsync();

        orphans.Should().BeEmpty();
    }

    private async Task<bool> TriggerExistsAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = @name AND NOT tgisinternal)";
        command.Parameters.AddWithValue("name", "trg_test_users_notify");
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
