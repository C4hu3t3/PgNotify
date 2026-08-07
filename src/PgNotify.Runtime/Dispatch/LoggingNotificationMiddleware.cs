using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PgNotify.Dispatch;

/// <summary>Logs every notification received and how long it took to dispatch. Added via <c>options.UseLogging()</c>.</summary>
public sealed class LoggingNotificationMiddleware(ILogger<LoggingNotificationMiddleware> logger) : INotificationMiddleware
{
    /// <inheritdoc />
    public async Task InvokeAsync(NotificationContext context, NotificationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var envelope = context.Envelope;
        logger.LogDebug(
            "Received notification on channel {Channel}: {Entity} {Operation}",
            envelope.Channel, envelope.Entity, envelope.Operation);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);
            logger.LogDebug(
                "Dispatched notification on channel {Channel} in {ElapsedMilliseconds}ms",
                envelope.Channel, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Error dispatching notification on channel {Channel} after {ElapsedMilliseconds}ms",
                envelope.Channel, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }
}
