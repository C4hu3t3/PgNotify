namespace PgNotify.Reconnect;

/// <summary>
/// Exponential backoff with jitter: delay doubles each attempt up to <see cref="MaxDelay"/>, plus
/// up to 30% random jitter to avoid many listeners reconnecting in lockstep after a shared outage.
/// </summary>
public sealed class ExponentialBackoffReconnectPolicy(
    TimeSpan? baseDelay = null,
    TimeSpan? maxDelay = null,
    int? maxAttempts = null) : IReconnectPolicy
{
    /// <summary>The delay before the first reconnect attempt. Defaults to 1 second.</summary>
    public TimeSpan BaseDelay { get; } = baseDelay ?? TimeSpan.FromSeconds(1);

    /// <summary>The maximum delay between attempts, before jitter. Defaults to 1 minute.</summary>
    public TimeSpan MaxDelay { get; } = maxDelay ?? TimeSpan.FromMinutes(1);

    /// <summary>The maximum number of attempts before giving up, or <see langword="null"/> for unlimited retries.</summary>
    public int? MaxAttempts { get; } = maxAttempts;

    /// <inheritdoc />
    public TimeSpan? GetDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1, nameof(attempt));

        if (MaxAttempts is { } max && attempt > max)
        {
            return null;
        }

        var exponent = Math.Min(attempt - 1, 20); // cap to avoid Math.Pow overflow
        var raw = BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(raw, MaxDelay.TotalMilliseconds);
        var jitterMilliseconds = Random.Shared.NextDouble() * 0.3 * capped;

        return TimeSpan.FromMilliseconds(capped + jitterMilliseconds);
    }
}
