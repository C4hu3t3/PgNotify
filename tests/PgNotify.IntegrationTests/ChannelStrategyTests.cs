using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Runs the three non-default channel strategies against a real server. Channel names are decided
/// in .NET at migration time and again in .NET at LISTEN time, from two different code paths — a
/// mismatch between them produces no error anywhere, just silence, which is precisely what
/// asserting on generated SQL text cannot catch.
/// </summary>
public sealed class ChannelStrategyTests : IAsyncLifetime
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AbsenceTimeout = TimeSpan.FromSeconds(3);

    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<ChannelStrategyDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        await using (var context = new ChannelStrategyDbContext(options))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ChannelStrategyDbContext>(o => o.UseNpgsql(_connectionString).UseNpgsqlNotifications());

        // Every channel name under test is derived from the model rather than repeated here, which
        // is the point: the strategies decide them, and the listener reads the same decision.
        services.AddPostgresNotifications(o => o.AddNotificationMappingFromDbContexts());

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

    private ChannelStrategyDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ChannelStrategyDbContext>().UseNpgsql(_connectionString).Options);

    private static async Task<NotificationEnvelope?> WaitForAsync(Task<NotificationEnvelope?> pending)
    {
        try
        {
            return await pending;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private Task<NotificationEnvelope?> ListenAsync<TEntity>(NotificationOperation operation, CancellationToken cancellationToken)
    {
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();

        // Started here rather than inside a Task.Run so the subscriber is registered before this
        // method returns, and therefore before the caller writes anything.
        return FirstAsync(notifications, cancellationToken);

        async Task<NotificationEnvelope?> FirstAsync(IPostgresNotificationService notifications, CancellationToken cancellationToken)
        {
            await foreach (var envelope in notifications.Events<TEntity>(operation, cancellationToken))
            {
                return envelope;
            }

            return null;
        }
    }

    [Fact]
    public async Task The_topic_strategy_puts_each_operation_on_its_own_channel()
    {
        await using var context = CreateContext();
        using var cts = new CancellationTokenSource(EventTimeout);

        var insertedWait = ListenAsync<TopicEntity>(NotificationOperation.Insert, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var entity = new TopicEntity { Name = "topical" };
        context.Topics.Add(entity);
        await context.SaveChangesAsync();

        var inserted = await WaitForAsync(insertedWait);

        var deletedWait = ListenAsync<TopicEntity>(NotificationOperation.Delete, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        context.Topics.Remove(entity);
        await context.SaveChangesAsync();

        var deleted = await WaitForAsync(deletedWait);

        inserted.Should().NotBeNull("topicentity.created must be listened to and notified on");
        inserted!.Id().Should().Be(entity.Id);
        deleted.Should().NotBeNull("topicentity.deleted is a different channel from the insert one");
        deleted!.Id().Should().Be(entity.Id);
    }

    [Fact]
    public async Task Two_entities_sharing_one_channel_still_route_to_their_own_streams()
    {
        // The routing key is (channel, entity), so a shared channel must not blur two entities
        // together - and a beta must never be delivered as an alpha.
        await using var context = CreateContext();
        using var cts = new CancellationTokenSource(EventTimeout);

        var alphaWait = ListenAsync<SharedAlpha>(NotificationOperation.Insert, cts.Token);
        var strayAlphaWait = ListenAsync<SharedAlpha>(NotificationOperation.Insert, new CancellationTokenSource(AbsenceTimeout).Token);
        var betaWait = ListenAsync<SharedBeta>(NotificationOperation.Insert, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var beta = new SharedBeta();
        context.Betas.Add(beta);
        await context.SaveChangesAsync();

        var receivedBeta = await WaitForAsync(betaWait);

        // Nothing was written to shared_alphas, so despite sharing the channel with the beta above,
        // no SharedAlpha notification may show up.
        var strayAlpha = await WaitForAsync(strayAlphaWait);

        var alpha = new SharedAlpha();
        context.Alphas.Add(alpha);
        await context.SaveChangesAsync();

        var receivedAlpha = await WaitForAsync(alphaWait);

        receivedBeta.Should().NotBeNull();
        receivedBeta!.Id().Should().Be(beta.Id);
        strayAlpha.Should().BeNull("a beta on the shared channel is not an alpha");
        receivedAlpha.Should().NotBeNull();
        receivedAlpha!.Id().Should().Be(alpha.Id);
    }

    [Fact]
    public async Task An_explicit_channel_name_overrides_the_strategy()
    {
        await using var context = CreateContext();
        using var cts = new CancellationTokenSource(EventTimeout);

        var wait = ListenAsync<OverriddenChannelEntity>(NotificationOperation.Insert, cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var entity = new OverriddenChannelEntity();
        context.Overridden.Add(entity);
        await context.SaveChangesAsync();

        var received = await WaitForAsync(wait);

        received.Should().NotBeNull("the trigger must notify on a_named_channel, not on the table name");
        received!.Id().Should().Be(entity.Id);
    }
}
