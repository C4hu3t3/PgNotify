using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Reconnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Verifies the listener survives having its underlying connection forcibly killed
/// (<c>pg_terminate_backend</c>, simulating a network blip or server restart) and resumes
/// delivering notifications afterward. Uses its own container/host, tagged with a distinct
/// <c>Application Name</c> so only the listener's connection — not the test's own EF Core
/// connections — gets terminated.
/// </summary>
public sealed class NotificationReconnectTests : IAsyncLifetime
{
    private const string ListenerApplicationName = "notifications-reconnect-test-listener";

    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;
    private RecordingReconnectPolicy _reconnectPolicy = null!;

    /// <summary>Wraps a real policy, recording every <c>attempt</c> value it is asked to delay for.</summary>
    private sealed class RecordingReconnectPolicy(IReconnectPolicy inner) : IReconnectPolicy
    {
        public List<int> Attempts { get; } = [];

        public TimeSpan? GetDelay(int attempt)
        {
            Attempts.Add(attempt);
            return inner.GetDelay(attempt);
        }
    }

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new IntegrationDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
        }

        var listenerConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            ApplicationName = ListenerApplicationName,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = listenerConnectionString;
            o.MapChannel<TestUser>("test_users");
            _reconnectPolicy = new RecordingReconnectPolicy(new ExponentialBackoffReconnectPolicy(
                baseDelay: TimeSpan.FromMilliseconds(200),
                maxDelay: TimeSpan.FromSeconds(2)));
            o.ReconnectPolicy = _reconnectPolicy;
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

    private async Task KillListenerConnectionAsync()
    {
        await using var adminConnection = new NpgsqlConnection(_connectionString);
        await adminConnection.OpenAsync();

        await using var command = adminConnection.CreateCommand();
        command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @applicationName;";
        command.Parameters.AddWithValue("applicationName", ListenerApplicationName);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Listener_reconnects_and_keeps_delivering_notifications_after_the_connection_is_killed()
    {
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();
        await using var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).Options);

        await KillListenerConnectionAsync();

        // Give the reconnect loop time to notice the failure and re-establish LISTEN.
        await Task.Delay(TimeSpan.FromSeconds(3));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var enumerator = notifications.Events<TestUser>(NotificationOperation.Insert, cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        var user = new TestUser { Name = "Ada", Email = "ada@reconnect.example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        (await moveNextTask).Should().BeTrue("the listener should have reconnected and be receiving notifications again");
        enumerator.Current.Id().Should().Be(user.Id);
    }

    [Fact]
    public async Task The_reconnect_attempt_counter_resets_after_a_successful_reconnect()
    {
        // Regression test: NpgsqlNotificationListener.RunAsync used to reset its attempt counter
        // only after the combined connect-and-listen method returned normally - which it never did,
        // since that method always ended by throwing (cancellation or a dropped connection). So the
        // counter climbed forever across every disconnect, even ones separated by a long stretch of
        // a healthy connection in between. Two independent disconnects, with a real successful
        // reconnect in between, must both start counting from attempt 1 - not 1 then 2.
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();
        await using var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_connectionString).Options);

        using var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstEnumerator = notifications.Events<TestUser>(NotificationOperation.Insert, firstCts.Token).GetAsyncEnumerator(firstCts.Token);

        await KillListenerConnectionAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));

        var firstMoveNextTask = firstEnumerator.MoveNextAsync();
        context.Users.Add(new TestUser { Name = "Ada", Email = "ada@reconnect-counter.example.com" });
        await context.SaveChangesAsync();

        (await firstMoveNextTask).Should().BeTrue("the first reconnect should have succeeded");

        using var secondCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var secondEnumerator = notifications.Events<TestUser>(NotificationOperation.Insert, secondCts.Token).GetAsyncEnumerator(secondCts.Token);

        await KillListenerConnectionAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));

        var secondMoveNextTask = secondEnumerator.MoveNextAsync();
        context.Users.Add(new TestUser { Name = "Grace", Email = "grace@reconnect-counter.example.com" });
        await context.SaveChangesAsync();

        (await secondMoveNextTask).Should().BeTrue("the second reconnect should have succeeded independently of the first");

        _reconnectPolicy.Attempts.Should().Equal(1, 1);
    }
}
