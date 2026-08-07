using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// The migrations-path counterpart to <see cref="DatabaseFirstTests"/>'s "watch-all-columns
/// reacts to the table's real column set" test: a deployed trigger for an unfiltered
/// <c>OnUpdate()</c> must keep watching every column as the model grows. Until the fingerprint was
/// derived from the generated SQL, a second migration adding a column emitted only the
/// <c>ALTER TABLE</c>, so the deployed function silently never fired for that column.
/// </summary>
public sealed class MigrationDiffRegenerationTests : IAsyncLifetime
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private DbContextOptions<T> Options<T>()
        where T : DbContext =>
        new DbContextOptionsBuilder<T>().UseNpgsql(_connectionString).UseNpgsqlNotifications().Options;

    private static async Task<NotificationEnvelope?> FirstMatchingEventAsync(
        IPostgresNotificationService notifications,
        Func<NotificationEnvelope, bool> predicate,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in notifications.Events<NoteEntity>(NotificationOperation.Update, cancellationToken))
        {
            if (predicate(envelope))
            {
                return envelope;
            }
        }

        return null;
    }

    [Fact]
    public async Task A_column_added_by_a_later_migration_is_watched_by_the_deployed_trigger()
    {
        // First migration: the model without Body (NotesDbContext ignores it).
        await using (var narrow = new NotesDbContext(Options<NotesDbContext>()))
        {
            await MigrationApplier.CreateSchemaAsync(narrow, _connectionString);
        }

        // Second migration: Body appears. Nothing about the notification configuration changed —
        // only the table did.
        await using (var narrow = new NotesDbContext(Options<NotesDbContext>()))
        await using (var wide = new NotesDbContextWithBody(Options<NotesDbContextWithBody>()))
        {
            await MigrationApplier.ApplyDiffAsync(narrow, wide, _connectionString);
        }

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

        await using var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(EventTimeout);
        var notifications = provider.GetRequiredService<IPostgresNotificationService>();

        // Subscribed before the write, and without Task.Run: the subscriber channel is registered
        // on the first MoveNextAsync, which happens before this call yields.
        var waitTask = FirstMatchingEventAsync(notifications, e => e.Id() == note.Id, cts.Token);

        // Body is the only column touched. It did not exist when the trigger was first deployed.
        note.Body = "Buy milk";
        await writeContext.SaveChangesAsync();

        NotificationEnvelope? received = null;
        try
        {
            received = await waitTask;
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        received.Should().NotBeNull("the trigger must have been regenerated to watch the added column");
        received!.Id().Should().Be(note.Id);
    }
}
