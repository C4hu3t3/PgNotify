using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// The end of the thread this branch started from: a typed event whose members all bind, because
/// the payload's shape was stated rather than inherited from a configuration default.
/// </summary>
public sealed class PayloadProjectionTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<ProjectedOrderDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        await using var context = new ProjectedOrderDbContext(options);
        await MigrationApplier.CreateSchemaAsync(context, _connectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static async Task<ProjectedOrderUpdated?> FirstEventAsync(
        IPostgresNotificationService notifications,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in notifications.Events<ProjectedOrder>(NotificationOperation.Update, cancellationToken))
        {
            // The projection's whole point: a hand-written shape binds against it, in the handler
            // or stream that knows which projection was configured.
            return envelope.ToTyped<ProjectedOrderUpdated>();
        }

        return null;
    }

    [Fact]
    public async Task A_projected_payload_binds_every_member_of_the_typed_event()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<ProjectedOrder>("projected_orders");
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await using var writeContext = new ProjectedOrderDbContext(
            new DbContextOptionsBuilder<ProjectedOrderDbContext>().UseNpgsql(_connectionString).Options);

        var order = new ProjectedOrder { Status = "pending", Total = 12.50m, InternalNote = "not in the payload" };
        writeContext.Orders.Add(order);
        await writeContext.SaveChangesAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitTask = FirstEventAsync(provider.GetRequiredService<IPostgresNotificationService>(), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        order.Status = "shipped";
        order.Total = 99.99m;
        await writeContext.SaveChangesAsync();

        ProjectedOrderUpdated? received = null;
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

        received.Should().NotBeNull();
        received!.Id.Should().Be(order.Id, "the key is projected whether or not the selector asked for it");
        received.Status.Should().Be("shipped");
        received.Total.Should().Be(99.99m);
        received.ToString().Should().NotContain("not in the payload", "unselected columns stay out of the payload");
    }
}
