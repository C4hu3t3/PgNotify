using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PgNotify;
using PgNotify.IntegrationTests.TestModels;
using PgNotify.Serialization;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// The exact shape that motivated <see cref="NotificationMappingBuilder.UseConnection"/>: a
/// <c>DbContext</c> configured via <c>UseNpgsql(NpgsqlDataSource, ...)</c> rather than a literal
/// connection string. An <c>NpgsqlDataSource</c>'s own <c>ConnectionString</c> omits the password
/// (Npgsql never round-trips it once bound to a data source, regardless of what created the data
/// source), so deriving the listener's connection the old way -
/// <c>context.Database.GetConnectionString()</c> alone - would produce a string the listener can
/// never actually authenticate with. This proves the derived listener connects and receives
/// notifications anyway, against a container that enforces real password authentication (not
/// <c>trust</c>), so a silently password-less connection string would fail loudly here rather than
/// coincidentally working.
/// </summary>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class NpgsqlDataSourceMappingTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private NpgsqlDataSource _dataSource = null!;
    private ServiceProvider _services = null!;

    private static readonly ConcurrentBag<int> ReceivedUserIds = [];
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        ReceivedUserIds.Clear();
        _armed = true;

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new IntegrationDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, connectionString);
        }

        // Mirrors AddNpgsqlDataSource(connectionString, ...) without needing the DI-registration
        // package: the object it produces, and the password-stripped ConnectionString it reports,
        // are the same either way.
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _dataSource.ConnectionString.Should().NotContain(
            "Password", "the data source must actually exhibit the password-stripping this test exists to route around");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_dataSource);
        services.AddDbContext<IntegrationDbContext>((sp, o) => o
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .UseNpgsqlNotifications());

        services.AddPostgresNotifications(o =>
        {
            o.AddHandlersFromAssembly(typeof(DataSourceHandler).Assembly);
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
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class DataSourceHandler : IDatabaseInsertedHandler<TestUser>
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
    public async Task A_context_configured_via_UseNpgsql_NpgsqlDataSource_still_authenticates_the_listener()
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var user = new TestUser { Name = "Dana", Email = "dana@example.com" };

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
