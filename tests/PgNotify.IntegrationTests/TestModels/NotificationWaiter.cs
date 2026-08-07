using PgNotify;
using PgNotify.Serialization;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Waits for a notification on an entity's stream. Every integration test observes the runtime the
/// same way — <c>Events&lt;TEntity&gt;()</c>, filtered by operation — so the timeout handling and
/// the "assert it never arrives" case live here rather than in each test.
/// </summary>
internal static class NotificationWaiter
{
    public static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long "nothing arrived" is worth waiting for before believing it.</summary>
    public static readonly TimeSpan AbsenceTimeout = TimeSpan.FromSeconds(3);

    public static async Task<NotificationEnvelope?> TryWaitAsync<TEntity>(
        IPostgresNotificationService notifications,
        NotificationOperation operation,
        Func<NotificationEnvelope, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);

        using var cts = new CancellationTokenSource(timeout ?? EventTimeout);
        try
        {
            await foreach (var envelope in notifications.Events<TEntity>(operation, cts.Token))
            {
                if (predicate is null || predicate(envelope))
                {
                    return envelope;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }

        return null;
    }

    public static async Task<NotificationEnvelope> WaitAsync<TEntity>(
        IPostgresNotificationService notifications,
        NotificationOperation operation,
        Func<NotificationEnvelope, bool>? predicate = null,
        TimeSpan? timeout = null)
    {
        var envelope = await TryWaitAsync<TEntity>(notifications, operation, predicate, timeout).ConfigureAwait(false);
        return envelope
            ?? throw new TimeoutException(
                $"No {operation} notification for {typeof(TEntity).Name} arrived within {timeout ?? EventTimeout}.");
    }

    /// <summary>The row key of a minimal or projected payload, which both put it at the top level.</summary>
    public static int Id(this NotificationEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Keys["id"].GetInt32();
    }
}
