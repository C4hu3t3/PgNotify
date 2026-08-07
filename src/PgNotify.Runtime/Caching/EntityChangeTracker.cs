using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace PgNotify.Caching;

/// <summary>
/// The <see cref="IEntityChangeTracker"/> implementation for one entity: a
/// <see cref="CancellationTokenSource"/> swapped out on every change, plus an optional coalescing
/// window that collapses a burst of notifications (one row-level <c>NOTIFY</c> per row of a bulk
/// <c>UPDATE</c>) into a leading and a trailing invalidation.
/// </summary>
internal sealed class EntityChangeTracker : IEntityChangeTracker, IDisposable
{
    private readonly TimeSpan _coalesceWindow;
    private readonly ILogger _logger;
    private readonly string _entityName;
    private readonly Timer? _windowTimer;
    private readonly Lock _gate = new();

    private long _lastModifiedTicks = DateTimeOffset.UtcNow.UtcTicks;
    private CancellationTokenSource _cts = new();
    private bool _windowOpen;
    private bool _pending;
    private bool _disposed;

    public EntityChangeTracker(string entityName, TimeSpan coalesceWindow, ILogger logger)
    {
        _entityName = entityName;
        _coalesceWindow = coalesceWindow;
        _logger = logger;

        if (coalesceWindow > TimeSpan.Zero)
        {
            _windowTimer = new Timer(static state => ((EntityChangeTracker)state!).OnWindowElapsed(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public DateTimeOffset LastModified => new(Interlocked.Read(ref _lastModifiedTicks), TimeSpan.Zero);

    public IChangeToken GetChangeToken() => new CancellationChangeToken(Volatile.Read(ref _cts).Token);

    /// <summary>Records a change at <paramref name="changedAt"/> and invalidates the current token.</summary>
    public void MarkChanged(DateTimeOffset changedAt)
    {
        AdvanceLastModified(changedAt);

        if (_windowTimer is null)
        {
            Invalidate();
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_windowOpen)
            {
                // Inside an open window: don't invalidate now, but make sure a trailing
                // invalidation happens once it closes. Dropping this change outright would leave
                // a cache repopulated between the leading invalidation and now permanently stale.
                _pending = true;
                return;
            }

            _windowOpen = true;
            _windowTimer.Change(_coalesceWindow, Timeout.InfiniteTimeSpan);
        }

        // Leading edge, deliberately outside the lock: cancelling runs every registered change
        // callback (cache eviction, ChangeToken.OnChange handlers) inline on this thread, and
        // those must never run while holding a lock a concurrent MarkChanged needs.
        Invalidate();
    }

    private void OnWindowElapsed()
    {
        bool invalidate;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            invalidate = _pending;
            _pending = false;

            if (invalidate)
            {
                // Keep the window open for one more period: a burst long enough to span several
                // windows should produce one invalidation per window, not one per notification.
                _windowTimer!.Change(_coalesceWindow, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _windowOpen = false;
            }
        }

        if (invalidate)
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());

        try
        {
            previous.Cancel();
        }
        catch (AggregateException ex)
        {
            // A change callback threw. Swallowing is right here: the token *has* been invalidated,
            // and letting this propagate would either fail the whole notification dispatch (and be
            // retried by RetryNotificationMiddleware) or, from the timer thread, crash the process.
            _logger.LogError(ex, "One or more change-token callbacks threw while invalidating the change tracker for {Entity}.", _entityName);
        }

        // `previous` is deliberately not disposed. A concurrent GetChangeToken() may already hold
        // the reference and be about to read .Token — which throws ObjectDisposedException once
        // the source is disposed — and CancellationChangeToken registers its callbacks on that
        // same source. Cancelled sources with no live registrations are cheap and collectible.
    }

    private void AdvanceLastModified(DateTimeOffset changedAt)
    {
        // Trigger timestamps come from concurrent database transactions and can arrive slightly
        // out of order; LastModified feeds ETags, so it must be monotonic.
        var ticks = changedAt.UtcTicks;
        var current = Interlocked.Read(ref _lastModifiedTicks);

        while (ticks > current)
        {
            var observed = Interlocked.CompareExchange(ref _lastModifiedTicks, ticks, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _windowTimer?.Dispose();
    }
}
