using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PgNotify.Internal;

/// <summary>
/// Fans a stream of <typeparamref name="T"/> out to any number of concurrent
/// <see cref="Subscribe"/> callers, each getting every value published after it subscribed (a hot
/// observable), backed by one <see cref="Channel{T}"/> per subscriber.
/// </summary>
internal sealed class EventBroadcaster<T>
{
    private readonly ConcurrentDictionary<Guid, Channel<T>> _subscribers = new();

    public IAsyncEnumerable<T> Subscribe(CancellationToken cancellationToken) => SubscribeCore(cancellationToken);

    public void Publish(T value)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(value);
        }
    }

    private async IAsyncEnumerable<T> SubscribeCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _subscribers[id] = channel;

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
