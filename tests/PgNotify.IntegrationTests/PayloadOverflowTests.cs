using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// The one that matters: an oversized payload raises inside the trigger, which aborts the
/// transaction that wrote the row. Proves a large row still writes, and that the notification it
/// produces says it was reduced.
/// </summary>
public sealed class PayloadOverflowTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<BigDocDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        await using var context = new BigDocDbContext(options);
        await MigrationApplier.CreateSchemaAsync(context, _connectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static async Task<List<BigDocInserted>> CollectAsync(
        IPostgresNotificationService notifications,
        int count,
        CancellationToken cancellationToken)
    {
        var received = new List<BigDocInserted>();

        try
        {
            await foreach (var envelope in notifications.Events<BigDoc>(NotificationOperation.Insert, cancellationToken))
            {
                received.Add(envelope.ToTyped<BigDocInserted>());
                if (received.Count == count)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return received;
    }

    [Fact]
    public async Task An_oversized_payload_reduces_the_notification_instead_of_failing_the_write()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<BigDoc>("big_docs");
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var collector = CollectAsync(provider.GetRequiredService<IPostgresNotificationService>(), 2, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await using var writeContext = new BigDocDbContext(
            new DbContextOptionsBuilder<BigDocDbContext>().UseNpgsql(_connectionString).Options);

        var small = new BigDoc { Body = "short" };
        writeContext.Docs.Add(small);
        await writeContext.SaveChangesAsync();

        // 10 kB of body, which the configured payload carries in full: without the guard this
        // pg_notify raises 22023 inside the trigger and takes the INSERT down with it.
        var large = new BigDoc { Body = new string('x', 10_000) };
        writeContext.Docs.Add(large);

        var write = async () => await writeContext.SaveChangesAsync();
        await write.Should().NotThrowAsync("an oversized notification payload must never abort the write");

        var received = await collector;

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        received.Should().HaveCount(2);
        var complete = received.Should().ContainSingle(e => !e.Truncated).Subject;
        complete.Body.Should().Be("short", "the small row fits, so its payload is the configured one");

        var reduced = received.Should().ContainSingle(e => e.Truncated).Subject;
        reduced.Id.Should().Be(large.Id, "a reduced payload still has to identify its row");
        reduced.Body.Should().BeNull("the reduced payload drops everything but the key");
    }
}
