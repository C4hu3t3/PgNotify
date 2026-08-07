using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Verifies that stopping the notification host waits for a dispatch already in flight to finish,
/// rather than the host tearing down (and disposing whatever DI scope that dispatch depends on)
/// out from under it. Uses its own dedicated host since it needs a handler class registered via
/// <c>AddHandlersFromAssembly</c>.
/// </summary>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class NotificationGracefulShutdownTests : IAsyncLifetime
{
    private IntegrationHost? _host;

    // AddHandlersFromAssembly registers this test's private handler into every other host that
    // scans this assembly too (see AssemblyScannedHandlerCollection), so it stays inert until this
    // test arms it.
    private static readonly TaskCompletionSource HandlerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static volatile bool _armed;
    private static volatile bool _handlerCompleted;

    public async Task InitializeAsync()
    {
        _armed = true;
        _handlerCompleted = false;
        _host = await IntegrationHost.StartAsync(o => o.AddHandlersFromAssembly(typeof(SlowShutdownHandler).Assembly));
    }

    public async Task DisposeAsync()
    {
        _armed = false;
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private sealed class SlowShutdownHandler : IDatabaseInsertedHandler<TestUser>
    {
        public async Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (!_armed)
            {
                return;
            }

            HandlerStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            _handlerCompleted = true;
        }
    }

    [Fact]
    public async Task Stopping_the_host_waits_for_an_in_flight_dispatch_to_finish_before_returning()
    {
        // Regression test: NpgsqlNotificationListener.OnNotification used to fire each dispatch via
        // an untracked Task.Run, so PostgresNotificationHostedService.StopAsync had no way to know
        // one was still running - it returned as soon as the listener's read loop stopped, even
        // while a handler was mid-flight.
        var options = new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_host!.ConnectionString).Options;
        await using var context = new IntegrationDbContext(options);

        context.Users.Add(new TestUser { Name = "Slow", Email = "slow-shutdown@example.com" });
        await context.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await HandlerStarted.Task.WaitAsync(cts.Token);

        var stopwatch = Stopwatch.StartNew();
        await _host.DisposeAsync();
        stopwatch.Stop();
        _host = null;

        _handlerCompleted.Should().BeTrue("stopping the host must have waited for the in-flight dispatch to finish");
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
    }
}
