using Microsoft.EntityFrameworkCore;
using PgNotify;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Proves the database-first path (<c>EnsureNotificationTriggersAsync</c> /
/// <c>GenerateNotificationTriggersScript</c>) works against a table this test creates with plain
/// SQL — simulating a schema owned by Flyway/DbUp/an existing database — never touching
/// <c>dotnet ef migrations</c> or the differ-based migrations pipeline at all.
/// </summary>
public sealed class DatabaseFirstTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        // The table is created by "some other tool" here — plain SQL standing in for
        // Flyway/DbUp/a hand-maintained schema. No EF Core migration is involved.
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE test_users (
                    "Id" serial PRIMARY KEY,
                    "Name" text NOT NULL,
                    "Email" text NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new IntegrationDbContext(contextOptions))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<TestUser>("test_users");
        });

        _services = services.BuildServiceProvider();

        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    public async Task DisposeAsync()
    {
        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Inserting_a_row_into_a_table_created_outside_ef_migrations_still_fires_a_notification()
    {
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();
        await using var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).Options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = notifications.Events<TestUser>(NotificationOperation.Insert, cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        var user = new TestUser { Name = "Ada", Email = "ada@dbfirst.example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        (await moveNextTask).Should().BeTrue();
        enumerator.Current.Id().Should().Be(user.Id);
    }

    [Fact]
    public async Task EnsureNotificationTriggersAsync_can_be_called_again_without_error()
    {
        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using var context = new IntegrationDbContext(contextOptions);
        var act = () => context.Database.EnsureNotificationTriggersAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureNotificationTriggersAsync_skips_regenerating_the_function_when_nothing_changed()
    {
        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using var context = new IntegrationDbContext(contextOptions);
        await context.Database.EnsureNotificationTriggersAsync();
        var before = await GetFunctionXminAsync();

        await context.Database.EnsureNotificationTriggersAsync();
        var after = await GetFunctionXminAsync();

        after.Should().Be(before);
    }

    [Fact]
    public async Task EnsureNotificationTriggersAsync_regenerates_when_the_watched_columns_change()
    {
        var originalOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using (var context = new IntegrationDbContext(originalOptions))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        var before = await GetFunctionXminAsync();

        var changedOptions = new DbContextOptionsBuilder<IntegrationDbContextWatchingEmail>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using (var context = new IntegrationDbContextWatchingEmail(changedOptions))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        var after = await GetFunctionXminAsync();
        after.Should().NotBe(before);

        // Prove the regenerated function really watches Email now, not just that something changed:
        // IntegrationDbContext (used by the rest of this fixture) only watched Name, so an
        // An email-only update would not have notified before this second call.
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();
        await using var writeContext = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).Options);

        var user = new TestUser { Name = "Grace", Email = "grace@dbfirst.example.com" };
        writeContext.Users.Add(user);
        await writeContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = notifications.Events<TestUser>(NotificationOperation.Update, cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        user.Email = "grace.updated@dbfirst.example.com";
        await writeContext.SaveChangesAsync();

        (await moveNextTask).Should().BeTrue();
        enumerator.Current.Id().Should().Be(user.Id);
    }

    [Fact]
    public async Task EnsureNotificationTriggersAsync_recreates_a_trigger_that_was_manually_dropped()
    {
        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using var context = new IntegrationDbContext(contextOptions);
        await context.Database.EnsureNotificationTriggersAsync();

        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER trg_test_users_notify ON test_users";
            await command.ExecuteNonQueryAsync();
        }

        (await TriggerExistsAsync()).Should().BeFalse();

        await context.Database.EnsureNotificationTriggersAsync();

        (await TriggerExistsAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureNotificationTriggersAsync_regenerates_when_the_tables_actual_columns_differ_from_a_narrower_model()
    {
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Body exists on the physical table from the start, standing in for a database-first
            // table whose real schema has already moved ahead of some deployment's EF Core model.
            command.CommandText = """
                CREATE TABLE db_first_notes (
                    "Id" serial PRIMARY KEY,
                    "Title" text NOT NULL,
                    "Body" text NOT NULL DEFAULT ''
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var narrowOptions = new DbContextOptionsBuilder<NotesDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using (var context = new NotesDbContext(narrowOptions))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        var before = await GetFunctionXminAsync("fn_db_first_notes_notify");

        var wideOptions = new DbContextOptionsBuilder<NotesDbContextWithBody>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;
        using (var context = new NotesDbContextWithBody(wideOptions))
        {
            await context.Database.EnsureNotificationTriggersAsync();
        }

        var after = await GetFunctionXminAsync("fn_db_first_notes_notify");
        after.Should().NotBe(before);

        var commentHash = await GetFunctionCommentAsync("fn_db_first_notes_notify");
        commentHash.Should().MatchRegex("^[0-9a-f]{64}$");

        // Prove the regenerated function really watches Body now: insert a row (Body untouched),
        // then update only Body and confirm a notification fires.
        await using var writeContext = new NotesDbContextWithBody(
            new DbContextOptionsBuilder<NotesDbContextWithBody>().UseNpgsql(_connectionString).Options);
        var note = new NoteEntity { Title = "Reminder", Body = "" };
        writeContext.Notes.Add(note);
        await writeContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<NoteEntity>("db_first_notes");
        });

        await using var noteServices = services.BuildServiceProvider();
        foreach (var hostedService in noteServices.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        try
        {
            var notifications = noteServices.GetRequiredService<IPostgresNotificationService>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var enumerator = notifications.Events<NoteEntity>(NotificationOperation.Update, cts.Token).GetAsyncEnumerator(cts.Token);
            var moveNextTask = enumerator.MoveNextAsync();

            note.Body = "Updated body";
            await writeContext.SaveChangesAsync();

            (await moveNextTask).Should().BeTrue();
            enumerator.Current.Id().Should().Be(note.Id);
        }
        finally
        {
            foreach (var hostedService in noteServices.GetServices<IHostedService>())
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
        }
    }

    private async Task<string?> GetFunctionXminAsync(string functionName = "fn_test_users_notify")
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT xmin::text FROM pg_proc WHERE proname = @name";
        command.Parameters.AddWithValue("name", functionName);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<string?> GetFunctionCommentAsync(string functionName)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT obj_description(p.oid, 'pg_proc') FROM pg_proc p WHERE p.proname = @name";
        command.Parameters.AddWithValue("name", functionName);
        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<bool> TriggerExistsAsync(string triggerName = "trg_test_users_notify")
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = @name AND NOT tgisinternal)";
        command.Parameters.AddWithValue("name", triggerName);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
