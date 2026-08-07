using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PgNotify.Dispatch;

/// <summary>
/// Retries a failed dispatch (handler threw, or a downstream middleware did) with a short linear
/// backoff, up to <see cref="PostgresNotificationsOptions.MaxRetryAttempts"/> attempts. Added via
/// <c>options.UseRetry(...)</c>.
/// </summary>
public sealed class RetryNotificationMiddleware(
    IOptions<PostgresNotificationsOptions> options,
    ILogger<RetryNotificationMiddleware> logger) : INotificationMiddleware
{
    /// <inheritdoc />
    public async Task InvokeAsync(NotificationContext context, NotificationDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var maxAttempts = Math.Max(1, options.Value.MaxRetryAttempts);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Notification dispatch failed on channel {Channel} (attempt {Attempt}/{MaxAttempts});",
                    context.Envelope.Channel, attempt, maxAttempts);

                if (attempt == maxAttempts)
                {
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(100 * attempt);
                await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }
}
