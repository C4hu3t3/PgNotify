using PgNotify.Internal;

namespace PgNotify.Runtime.Tests.Internal;

public class EventBroadcasterTests
{
    [Fact]
    public async Task Subscribe_receives_values_published_after_subscribing()
    {
        var broadcaster = new EventBroadcaster<int>();
        using var cts = new CancellationTokenSource();

        var enumerator = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync();

        broadcaster.Publish(42);

        (await moveNextTask).Should().BeTrue();
        enumerator.Current.Should().Be(42);
    }

    [Fact]
    public async Task Publish_fans_out_to_multiple_concurrent_subscribers()
    {
        var broadcaster = new EventBroadcaster<string>();
        using var cts = new CancellationTokenSource();

        var enumerator1 = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var enumerator2 = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var move1 = enumerator1.MoveNextAsync();
        var move2 = enumerator2.MoveNextAsync();

        broadcaster.Publish("hello");

        (await move1).Should().BeTrue();
        (await move2).Should().BeTrue();
        enumerator1.Current.Should().Be("hello");
        enumerator2.Current.Should().Be("hello");
    }

    [Fact]
    public async Task Publish_with_no_subscribers_does_not_throw()
    {
        var broadcaster = new EventBroadcaster<int>();

        var act = () => broadcaster.Publish(1);

        act.Should().NotThrow();
        await Task.CompletedTask;
    }

    [Fact]
    public void Publish_to_a_subscriber_that_has_stopped_reading_does_not_block_or_throw()
    {
        var broadcaster = new EventBroadcaster<int>();
        using var cts = new CancellationTokenSource();

        // Registers the subscriber's channel (the first MoveNextAsync call runs the iterator up
        // to its await point), then leaves it parked there forever - simulating a consumer that
        // has fallen behind and stopped reading. Each subscriber's Channel<T> is unbounded, so
        // every subsequent Publish is still a non-blocking write regardless.
        var stalledEnumerator = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        _ = stalledEnumerator.MoveNextAsync();

        var act = () =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                broadcaster.Publish(i);
            }
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task An_actively_reading_subscriber_still_receives_values_promptly_despite_a_stalled_subscriber()
    {
        var broadcaster = new EventBroadcaster<int>();
        using var cts = new CancellationTokenSource();

        var stalledEnumerator = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var stalledPending = stalledEnumerator.MoveNextAsync();

        var activeEnumerator = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var activePending = activeEnumerator.MoveNextAsync();

        broadcaster.Publish(42);

        (await activePending).Should().BeTrue();
        activeEnumerator.Current.Should().Be(42);

        // The stalled subscriber still got its own independent copy via its own Channel<T> - it
        // just hadn't been read by the test yet, which never delayed the active subscriber above.
        (await stalledPending).Should().BeTrue();
        stalledEnumerator.Current.Should().Be(42);
    }

    [Fact]
    public async Task Cancelling_the_subscription_token_stops_enumeration()
    {
        var broadcaster = new EventBroadcaster<int>();
        using var cts = new CancellationTokenSource();

        var enumerator = broadcaster.Subscribe(cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync();

        await cts.CancelAsync();

        var act = async () => await moveNextTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
