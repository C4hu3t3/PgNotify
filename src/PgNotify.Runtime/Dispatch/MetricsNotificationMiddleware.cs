using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PgNotify.Dispatch;

/// <summary>
/// Records notification throughput and dispatch latency via <see cref="System.Diagnostics.Metrics"/>
/// (meter name <c>PgNotify</c>), with no external
/// dependency — consume it with any <c>System.Diagnostics.Metrics</c>-compatible exporter (e.g.
/// OpenTelemetry). Added via <c>options.UseMetrics()</c>.
/// </summary>
public sealed class MetricsNotificationMiddleware : INotificationMiddleware
{
    /// <summary>The meter name notification metrics are recorded under.</summary>
    public const string MeterName = "PgNotify";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ReceivedCounter = Meter.CreateCounter<long>("notifications.received", description: "Notifications received, before dispatch.");
    private static readonly Counter<long> FailedCounter = Meter.CreateCounter<long>("notifications.failed", description: "Notifications whose dispatch threw an exception.");
    private static readonly Histogram<double> DispatchDuration = Meter.CreateHistogram<double>("notifications.dispatch.duration", unit: "ms", description: "Time spent dispatching a notification.");

    /// <inheritdoc />
    public async Task InvokeAsync(NotificationContext context, NotificationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var tag = new KeyValuePair<string, object?>("channel", context.Envelope.Channel);
        ReceivedCounter.Add(1, tag);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            FailedCounter.Add(1, tag);
            throw;
        }
        finally
        {
            DispatchDuration.Record(stopwatch.Elapsed.TotalMilliseconds, tag);
        }
    }
}
