using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using PgNotify.Caching;

namespace PgNotify.Runtime.Tests.Caching;

public class EntityChangeTrackerTests
{
    private static EntityChangeTracker CreateTracker(TimeSpan coalesceWindow = default) =>
        new("User", coalesceWindow, NullLogger.Instance);

    [Fact]
    public void The_current_token_fires_when_a_change_is_marked()
    {
        using var tracker = CreateTracker();
        var token = tracker.GetChangeToken();

        token.HasChanged.Should().BeFalse();

        tracker.MarkChanged(DateTimeOffset.UtcNow);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void A_token_obtained_after_a_change_is_fresh()
    {
        using var tracker = CreateTracker();
        tracker.MarkChanged(DateTimeOffset.UtcNow);

        tracker.GetChangeToken().HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Registered_change_callbacks_run_on_the_next_change()
    {
        using var tracker = CreateTracker();
        var invocations = 0;

        using var registration = tracker.GetChangeToken().RegisterChangeCallback(_ => invocations++, null);

        tracker.MarkChanged(DateTimeOffset.UtcNow);

        invocations.Should().Be(1);
    }

    [Fact]
    public void A_callback_that_throws_does_not_propagate_to_the_caller()
    {
        // The dispatch pipeline calls MarkChanged; a badly-behaved cache eviction callback must not
        // fail the notification (and get retried by RetryNotificationMiddleware).
        using var tracker = CreateTracker();
        using var registration = tracker.GetChangeToken().RegisterChangeCallback(_ => throw new InvalidOperationException("boom"), null);

        var act = () => tracker.MarkChanged(DateTimeOffset.UtcNow);

        act.Should().NotThrow();
        tracker.GetChangeToken().HasChanged.Should().BeFalse();
    }

    [Fact]
    public void LastModified_uses_the_supplied_timestamp_rather_than_local_time()
    {
        // This is what makes an ETag derived from LastModified identical on every instance behind
        // a load balancer: the value comes from the trigger's payload, not from receive time.
        using var tracker = CreateTracker();
        var changedAt = DateTimeOffset.UtcNow.AddSeconds(5);

        tracker.MarkChanged(changedAt);

        tracker.LastModified.Should().Be(changedAt.ToUniversalTime());
    }

    [Fact]
    public void LastModified_never_moves_backwards_when_timestamps_arrive_out_of_order()
    {
        using var tracker = CreateTracker();
        var later = DateTimeOffset.UtcNow.AddSeconds(10);

        tracker.MarkChanged(later);
        tracker.MarkChanged(later.AddSeconds(-5));

        tracker.LastModified.Should().Be(later.ToUniversalTime());
    }

    [Fact]
    public async Task Reading_tokens_concurrently_with_changes_never_throws()
    {
        // Regression guard for the obvious-but-wrong implementation: disposing the swapped-out
        // CancellationTokenSource makes a concurrent GetChangeToken() throw ObjectDisposedException
        // on .Token (and CancellationChangeToken throw when registering its callback).
        using var tracker = CreateTracker();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var token = tracker.GetChangeToken();
                token.RegisterChangeCallback(static _ => { }, null).Dispose();
            }
        });

        var writer = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                tracker.MarkChanged(DateTimeOffset.UtcNow);
            }
        });

        var act = async () => await Task.WhenAll(reader, writer);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Without_a_coalescing_window_every_change_invalidates()
    {
        using var tracker = CreateTracker();
        var invocations = 0;

        Watch(tracker, () => Interlocked.Increment(ref invocations));

        for (var i = 0; i < 5; i++)
        {
            tracker.MarkChanged(DateTimeOffset.UtcNow);
        }

        await Task.Yield();
        Volatile.Read(ref invocations).Should().Be(5);
    }

    [Fact]
    public async Task A_burst_inside_the_coalescing_window_produces_one_leading_and_one_trailing_invalidation()
    {
        var window = TimeSpan.FromMilliseconds(200);
        using var tracker = CreateTracker(window);
        var invocations = 0;

        Watch(tracker, () => Interlocked.Increment(ref invocations));

        for (var i = 0; i < 50; i++)
        {
            tracker.MarkChanged(DateTimeOffset.UtcNow);
        }

        // Leading edge is immediate, so a cache is never left serving stale data for the window.
        Volatile.Read(ref invocations).Should().Be(1);

        await WaitUntilAsync(() => Volatile.Read(ref invocations) >= 2, TimeSpan.FromSeconds(5));

        // The trailing invalidation covers the changes that arrived after the leading one; the
        // window then closes with nothing pending, so no further invalidation follows.
        await Task.Delay(window + window);
        Volatile.Read(ref invocations).Should().Be(2);
    }

    [Fact]
    public async Task A_single_change_inside_a_coalescing_window_still_invalidates_immediately()
    {
        using var tracker = CreateTracker(TimeSpan.FromMilliseconds(200));
        var invocations = 0;

        Watch(tracker, () => Interlocked.Increment(ref invocations));

        tracker.MarkChanged(DateTimeOffset.UtcNow);

        Volatile.Read(ref invocations).Should().Be(1);

        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Volatile.Read(ref invocations).Should().Be(1);
    }

    /// <summary>
    /// Re-registers on every fire, the way <c>ChangeToken.OnChange</c> does, so the callback
    /// counts invalidations rather than only the first one.
    /// </summary>
    private static void Watch(EntityChangeTracker tracker, Action onChange) =>
        ChangeToken.OnChange(tracker.GetChangeToken, onChange);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        condition().Should().BeTrue("the expected state was not reached within {0}", timeout);
    }
}
