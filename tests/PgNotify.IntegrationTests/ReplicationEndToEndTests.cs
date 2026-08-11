using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify;
using PgNotify.IntegrationTests.TestModels;
using PgNotify.Internal;
using PgNotify.Serialization;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Proves <see cref="NotificationDeliveryMode.LogicalReplication"/> end to end against a real
/// server: insert/update/delete dispatched through the same handler interfaces and
/// <c>Events&lt;TEntity&gt;()</c> streams the NOTIFY path uses (see
/// <see cref="Internal.NotificationPublisher"/> and <c>docs/plans/logical-replication-delivery.md</c>),
/// and — the property this delivery mode exists for — a notification made while the listener is
/// stopped is not lost when it restarts, without redelivering one already confirmed.
/// </summary>
/// <remarks>
/// Uses its own container, configured with <c>wal_level=logical</c>, rather than the shared
/// <see cref="NotificationHostFixture"/>: every other integration test's container has no reason to
/// pay for logical decoding support it never uses. Joins <see cref="AssemblyScannedHandlerCollection"/>
/// because its handler records into static state, the same reason
/// <see cref="ModelDrivenMappingTests"/> does.
/// </remarks>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class ReplicationEndToEndTests : IAsyncLifetime
{
    // ReplicationDbContext uses the extended payload (to get the "changed" field), which nests the
    // key under "keys" using the column name ("Id"), unlike the minimal shape
    // NotificationWaiter.Id() assumes (a top-level "id").
    private static int OrderId(NotificationEnvelope envelope) => envelope.Keys["Id"].GetInt32();

    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    private static readonly ConcurrentQueue<(NotificationOperation Operation, int Id)> Received = new();
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        Received.Clear();
        _armed = true;

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithCommand("-c", "wal_level=logical", "-c", "max_replication_slots=4", "-c", "max_wal_senders=4")
            .Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<ReplicationDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new ReplicationDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ReplicationDbContext>(o => o
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications());

        services.AddPostgresNotifications(o => o.ConnectionString = _connectionString);
        services.AddPostgresLogicalReplication(o =>
        {
            o.AddReplicationMappingFromDbContexts();
        });
        services.AddScoped<IDatabaseInsertedHandler<ReplicationOrder>, RecordingHandler>();
        services.AddScoped<IDatabaseUpdatedHandler<ReplicationOrder>, RecordingHandler>();
        services.AddScoped<IDatabaseDeletedHandler<ReplicationOrder>, RecordingHandler>();

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

    private sealed class RecordingHandler
        : IDatabaseInsertedHandler<ReplicationOrder>, IDatabaseUpdatedHandler<ReplicationOrder>, IDatabaseDeletedHandler<ReplicationOrder>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (_armed)
            {
                Received.Enqueue((envelope.Operation, OrderId(envelope)));
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Insert_update_delete_are_dispatched_through_the_shared_pipeline()
    {
        var notifications = _services.GetRequiredService<IPostgresNotificationService>();

        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ReplicationDbContext>();
        var order = new ReplicationOrder { Status = "new" };

        // The wait must start before the write it is waiting for: Events<TEntity>() has no
        // history/replay (see IPostgresNotificationService's remarks), so subscribing after
        // SaveChangesAsync() races the replication listener, which may already have dispatched by
        // the time this method gets around to awaiting the stream. Same convention
        // NotificationEndToEndTests already uses for the NOTIFY path. No predicate for the insert:
        // order.Id is a database-generated identity value, not known until after it is saved.
        var insertWait = NotificationWaiter.WaitAsync<ReplicationOrder>(notifications, NotificationOperation.Insert);
        context.Orders.Add(order);
        await context.SaveChangesAsync();

        var inserted = await insertWait;
        inserted.Entity.Should().Be("ReplicationOrder");
        OrderId(inserted).Should().Be(order.Id);

        var updateWait = NotificationWaiter.WaitAsync<ReplicationOrder>(
            notifications, NotificationOperation.Update, e => OrderId(e) == order.Id);
        order.Status = "shipped";
        await context.SaveChangesAsync();

        var updated = await updateWait;
        updated.Changed.Should().Contain("Status");

        var deleteWait = NotificationWaiter.WaitAsync<ReplicationOrder>(
            notifications, NotificationOperation.Delete, e => OrderId(e) == order.Id);
        context.Orders.Remove(order);
        await context.SaveChangesAsync();

        var deleted = await deleteWait;
        deleted.Operation.Should().Be(NotificationOperation.Delete);

        Received.Should().Contain((NotificationOperation.Insert, order.Id));
        Received.Should().Contain((NotificationOperation.Update, order.Id));
        Received.Should().Contain((NotificationOperation.Delete, order.Id));
    }

    [Fact]
    public async Task Stopping_and_restarting_the_replication_listener_loses_nothing_and_redelivers_nothing_already_confirmed()
    {
        var logicalReplicationService = _services.GetServices<IHostedService>()
            .OfType<LogicalReplicationHostedService>()
            .Single();

        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ReplicationDbContext>();

        var confirmedOrder = new ReplicationOrder { Status = "new" };
        context.Orders.Add(confirmedOrder);
        await context.SaveChangesAsync();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!Received.Contains((NotificationOperation.Insert, confirmedOrder.Id)) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Received.Should().Contain((NotificationOperation.Insert, confirmedOrder.Id));

        // Stop only the replication listener -- the NOTIFY listener and the rest of the host stay
        // up, matching what a process restart of just this component looks like.
        await logicalReplicationService.StopAsync(CancellationToken.None);

        var missedOrder = new ReplicationOrder { Status = "new" };
        context.Orders.Add(missedOrder);
        await context.SaveChangesAsync();

        await logicalReplicationService.StartAsync(CancellationToken.None);

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!Received.Contains((NotificationOperation.Insert, missedOrder.Id)) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Received.Should().Contain((NotificationOperation.Insert, missedOrder.Id), "the row inserted while stopped must not be lost");
        Received.Count(e => e == (NotificationOperation.Insert, confirmedOrder.Id)).Should()
            .Be(1, "a transaction already confirmed before the stop must not replay");
    }
}
