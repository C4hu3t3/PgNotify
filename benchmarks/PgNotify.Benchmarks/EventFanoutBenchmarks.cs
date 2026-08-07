using BenchmarkDotNet.Attributes;
using PgNotify.Internal;
using PgNotify.Serialization;

namespace PgNotify.Benchmarks;

/// <summary>
/// Measures <see cref="NotificationEventHub.Publish"/> fan-out cost as the number of concurrent
/// <c>Events&lt;TEntity&gt;()</c> subscribers grows, per the tradeoff called out in
/// <c>docs/performance.md</c>: each subscriber gets its own <see cref="System.Threading.Channels.Channel{T}"/>,
/// so publishing is one write per subscriber, not a single broadcast. Each invocation publishes
/// one value and waits for every subscriber to observe it, keeping per-subscriber queues drained
/// so memory stays bounded across iterations.
/// </summary>
[MemoryDiagnoser]
public class EventFanoutBenchmarks
{
    private static readonly NotificationEnvelope Envelope = new()
    {
        Channel = "users",
        Entity = "User",
        Operation = NotificationOperation.Update,
        Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
        RawPayload = """{"entity":"User","operation":"updated","id":42}""",
    };

    [Params(1, 10, 100)]
    public int SubscriberCount { get; set; }

    private NotificationEventHub _hub = null!;
    private CancellationTokenSource _cts = null!;
    private List<IAsyncEnumerator<NotificationEnvelope>> _enumerators = null!;
    private List<ValueTask<bool>> _pending = null!;

    [GlobalSetup]
    public void Setup()
    {
        _hub = new NotificationEventHub();
        _cts = new CancellationTokenSource();
        _enumerators = [];
        _pending = [];

        for (var i = 0; i < SubscriberCount; i++)
        {
            var enumerator = _hub.Subscribe(typeof(User), _cts.Token).GetAsyncEnumerator();
            _enumerators.Add(enumerator);
            _pending.Add(enumerator.MoveNextAsync());
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    [Benchmark]
    public async Task PublishToAllSubscribers()
    {
        _hub.Publish(typeof(User), Envelope);

        for (var i = 0; i < _enumerators.Count; i++)
        {
            await _pending[i].ConfigureAwait(false);
            _pending[i] = _enumerators[i].MoveNextAsync();
        }
    }
}
