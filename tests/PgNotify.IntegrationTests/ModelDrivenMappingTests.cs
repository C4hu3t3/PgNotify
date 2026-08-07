using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// The listening side configured with nothing at all: no channel names, no connection string —
/// only a <c>DbContext</c> that opted in. Everything else is derived from its model when the host
/// starts, and this proves the derived values actually work against a server rather than merely
/// looking right in an assertion.
/// </summary>
/// <remarks>
/// The context's connection string deliberately carries <c>Pooling=true;MaxPoolSize=1</c>, which
/// makes the sanitizing in <c>NotificationConnectionString.ForListening</c> load-bearing here:
/// inherited verbatim, the listener's permanently-held <c>LISTEN</c> connection is the one slot of
/// the pool the application itself needs, and the <c>INSERT</c> below cannot even run. (The other
/// half of the sanitizing, <c>Multiplexing</c>, is covered by unit tests: measured, a multiplexed
/// listener still receives its notifications — it just reconnects several times a second doing it.)
/// </remarks>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class ModelDrivenMappingTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    private static readonly ConcurrentBag<int> ReceivedUserIds = [];
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        ReceivedUserIds.Clear();
        _armed = true;

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

        var applicationConnectionString = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            Timeout = 3,
        }.ToString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<IntegrationDbContext>(o => o
            .UseNpgsql(applicationConnectionString)
            .UseNpgsqlNotifications());

        // No ConnectionString, no MapChannel: both come from the context above.
        services.AddPostgresNotifications(o =>
        {
            o.AddHandlersFromAssembly(typeof(ModelDrivenHandler).Assembly);
            o.AddNotificationMappingFromDbContexts();
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
        _armed = false;

        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class ModelDrivenHandler : IDatabaseInsertedHandler<TestUser>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (_armed)
            {
                ReceivedUserIds.Add(envelope.Keys["id"].GetInt32());
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_handler_receives_notifications_on_a_channel_nobody_named()
    {
        // Written through the application's own context, so it competes for the same pool the
        // listener would take over if its connection string were inherited verbatim.
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var user = new TestUser { Name = "Grace", Email = "grace@example.com" };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ReceivedUserIds.Contains(user.Id) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        ReceivedUserIds.Should().Contain(user.Id);
    }
}
