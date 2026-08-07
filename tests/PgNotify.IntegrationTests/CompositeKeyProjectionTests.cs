using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Executes the two projected payload shapes the unit tests can only assert as text: a composite
/// key rendered as a <c>keys</c> object inside a projection, and a projected column read off
/// <c>OLD</c> on delete.
/// </summary>
public sealed class CompositeKeyProjectionTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<ProjectedLineDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        await using var context = new ProjectedLineDbContext(options);
        await MigrationApplier.CreateSchemaAsync(context, _connectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static async Task<NotificationEnvelope?> FirstEventAsync(
        IPostgresNotificationService notifications,
        NotificationOperation operation,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in notifications.Events<ProjectedLine>(operation, cancellationToken))
        {
            return envelope;
        }

        return null;
    }

    private static async Task<T?> AwaitOrNullAsync<T>(Task<T?> task)
        where T : class
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_composite_key_projection_round_trips_on_insert_and_delete()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<ProjectedLine>("projected_lines");
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var notifications = provider.GetRequiredService<IPostgresNotificationService>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var writeContext = new ProjectedLineDbContext(
            new DbContextOptionsBuilder<ProjectedLineDbContext>().UseNpgsql(_connectionString).Options);

        var insertedWait = FirstEventAsync(notifications, NotificationOperation.Insert, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var line = new ProjectedLine { OrderId = 7, LineNumber = 3, Description = "widget", Quantity = 2 };
        writeContext.Lines.Add(line);
        await writeContext.SaveChangesAsync();

        var inserted = await AwaitOrNullAsync(insertedWait);

        var deletedWait = FirstEventAsync(notifications, NotificationOperation.Delete, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        writeContext.Lines.Remove(line);
        await writeContext.SaveChangesAsync();

        var deleted = await AwaitOrNullAsync(deletedWait);

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        inserted.Should().NotBeNull();
        var insertedShape = inserted!.ToTyped<ProjectedLineShape>();
        insertedShape.Keys.Should().BeEquivalentTo(new Dictionary<string, int> { ["OrderId"] = 7, ["LineNumber"] = 3 });
        insertedShape.Description.Should().Be("widget");

        deleted.Should().NotBeNull("the delete branch must read the projected column off OLD");
        var deletedShape = deleted!.ToTyped<ProjectedLineShape>();
        deletedShape.Keys.Should().BeEquivalentTo(new Dictionary<string, int> { ["OrderId"] = 7, ["LineNumber"] = 3 });
        deletedShape.Description.Should().Be("widget", "OLD still holds the deleted row's values");
    }
}
