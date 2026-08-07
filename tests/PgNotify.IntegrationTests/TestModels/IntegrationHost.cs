using Microsoft.EntityFrameworkCore;
using PgNotify;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// A running PostgreSQL container with the notification schema applied and the runtime listener
/// started, without depending on the generic <c>IHost</c>/<c>HostBuilder</c> (hosted services are
/// started/stopped manually, which is the standard way to exercise them in tests).
/// </summary>
public sealed class IntegrationHost : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private readonly ServiceProvider _services;

    private IntegrationHost(PostgreSqlContainer container, ServiceProvider services, string connectionString)
    {
        _container = container;
        _services = services;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public IPostgresNotificationService Notifications => _services.GetRequiredService<IPostgresNotificationService>();

    public IServiceScope CreateScope() => _services.CreateScope();

    public static async Task<IntegrationHost> StartAsync(
        Action<PostgresNotificationsOptions>? configureNotifications = null,
        CancellationToken cancellationToken = default)
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);
        var connectionString = container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseNpgsql(connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new IntegrationDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, connectionString, cancellationToken).ConfigureAwait(false);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = connectionString;

            // The entity's channel: handlers and Events<TestUser>() streams are keyed on the same
            // type, so one line covers both.
            o.MapChannel<TestUser>("test_users");
            configureNotifications?.Invoke(o);
        });

        var provider = services.BuildServiceProvider();

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        // Give the listener a moment to establish LISTEN before the test starts publishing.
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        return new IntegrationHost(container, provider, connectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await _services.DisposeAsync().ConfigureAwait(false);
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
