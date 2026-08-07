using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Verifies the claim in <c>docs/architecture.md</c> that dispatch happens on a background loop
/// reading one dedicated connection, and that <c>NpgsqlNotificationListener</c> dispatches each
/// received notification via an un-awaited <c>Task.Run</c> rather than sequentially on the
/// listener's read loop - so a slow handler for one notification must not delay the handler for
/// a later, unrelated notification. Uses its own dedicated host (distinct from
/// <see cref="HandlerDispatchTests"/>) since it needs its own handler class registered via
/// <c>AddHandlersFromAssembly</c>.
/// </summary>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class SlowHandlerIsolationTests : IAsyncLifetime
{
    private IntegrationHost _host = null!;

    private static readonly ConcurrentBag<int> ReceivedUserIds = [];
    private static readonly TaskCompletionSource SlowHandlerGate = new();
    private static int _handlerCallCount;

    // AddHandlersFromAssembly registers this test's private handler into every other host that
    // scans this assembly too (see AssemblyScannedHandlerCollection), where it would count rows
    // from a different container into the static fields below - and, worse, block that host's own
    // handlers on the gate, since handlers for one notification run sequentially. It therefore
    // does nothing at all until this test arms it.
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        ReceivedUserIds.Clear();
        Interlocked.Exchange(ref _handlerCallCount, 0);
        _armed = true;

        _host = await IntegrationHost.StartAsync(o => o.AddHandlersFromAssembly(typeof(SlowFirstCallHandler).Assembly));
    }

    public async Task DisposeAsync()
    {
        _armed = false;
        SlowHandlerGate.TrySetResult();
        await _host.DisposeAsync();
    }

    private sealed class SlowFirstCallHandler : IDatabaseInsertedHandler<TestUser>
    {
        public async Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (!_armed)
            {
                return;
            }

            if (Interlocked.Increment(ref _handlerCallCount) == 1)
            {
                // Only the very first notification's handler blocks - simulates a slow consumer
                // without needing to know a row's id ahead of the insert that creates it.
                await SlowHandlerGate.Task;
            }

            ReceivedUserIds.Add(envelope.Keys["id"].GetInt32());
        }
    }

    [Fact]
    public async Task A_slow_handler_for_one_notification_does_not_delay_dispatch_of_a_later_notification()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_host.ConnectionString).Options;
        await using var context = new IntegrationDbContext(options);

        var slowUser = new TestUser { Name = "Slow", Email = "slow@example.com" };
        context.Users.Add(slowUser);
        await context.SaveChangesAsync();

        // Give the listener time to receive and start dispatching the first notification, whose
        // handler is now blocked awaiting SlowHandlerGate.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref _handlerCallCount) < 1 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Volatile.Read(ref _handlerCallCount).Should().Be(1, "the first notification's handler must have started (and blocked) by now");

        var fastUser = new TestUser { Name = "Fast", Email = "fast@example.com" };
        context.Users.Add(fastUser);
        await context.SaveChangesAsync();

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ReceivedUserIds.Contains(fastUser.Id) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        ReceivedUserIds.Should().Contain(fastUser.Id, "the second notification's handler must run even though the first is still blocked");
        ReceivedUserIds.Should().NotContain(slowUser.Id, "the first handler is still awaiting its gate and has not recorded its id yet");

        SlowHandlerGate.SetResult();

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!ReceivedUserIds.Contains(slowUser.Id) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        ReceivedUserIds.Should().Contain(slowUser.Id, "releasing the gate should let the first handler finish");
    }
}
