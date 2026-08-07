using Microsoft.EntityFrameworkCore;
using PgNotify;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Proves a custom <c>WithNamePrefix(...)</c> both (a) actually renames the real PostgreSQL
/// trigger/function objects, and (b) doesn't otherwise change notification delivery — the prefix
/// only affects object names, never the channel or payload.
/// </summary>
public sealed class NotificationNamePrefixIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<PrefixedIntegrationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new PrefixedIntegrationDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
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
    public async Task The_real_trigger_and_function_are_created_with_the_configured_prefix()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var triggerCommand = connection.CreateCommand();
        triggerCommand.CommandText = "SELECT tgname FROM pg_trigger WHERE NOT tgisinternal;";
        await using var triggerReader = await triggerCommand.ExecuteReaderAsync();
        (await triggerReader.ReadAsync()).Should().BeTrue();
        ((string)triggerReader["tgname"]).Should().Be($"{PrefixedIntegrationDbContext.NamePrefix}trg_test_users_notify");
        await triggerReader.CloseAsync();

        await using var functionCommand = connection.CreateCommand();
        functionCommand.CommandText = "SELECT proname FROM pg_proc WHERE proname LIKE '%_notify';";
        await using var functionReader = await functionCommand.ExecuteReaderAsync();
        (await functionReader.ReadAsync()).Should().BeTrue();
        ((string)functionReader["proname"]).Should().Be($"{PrefixedIntegrationDbContext.NamePrefix}fn_test_users_notify");
    }

    [Fact]
    public async Task Notifications_still_flow_normally_with_a_custom_prefix()
    {
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();
        await using var context = new PrefixedIntegrationDbContext(
            new DbContextOptionsBuilder<PrefixedIntegrationDbContext>().UseNpgsql(_connectionString).Options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var enumerator = notifications.Events<TestUser>(NotificationOperation.Insert, cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNextTask = enumerator.MoveNextAsync();

        var user = new TestUser { Name = "Ada", Email = "ada@prefix.example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        (await moveNextTask).Should().BeTrue();
        enumerator.Current.Id().Should().Be(user.Id);
    }
}
