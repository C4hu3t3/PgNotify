using Microsoft.Extensions.Logging;
using Npgsql;
using PgNotify.Reconnect;

namespace PgNotify.Internal;

/// <summary>
/// Owns a single, dedicated <see cref="NpgsqlConnection"/> used exclusively for
/// <c>LISTEN</c>/<c>NOTIFY</c> (never pooled: a <c>LISTEN</c> registration is connection-scoped,
/// so this connection must stay open and un-shared for the lifetime of the listener). Reconnects
/// automatically using the configured <see cref="IReconnectPolicy"/>.
/// </summary>
internal sealed class NpgsqlNotificationListener(
    NotificationRuntimeState state,
    IReconnectPolicy reconnectPolicy,
    ILogger<NpgsqlNotificationListener> logger) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private int _inFlightDispatchCount;
    private TaskCompletionSource? _drainTcs;

    /// <summary>Raised for every received notification, on the connection's event-processing context.</summary>
    public event Func<string, string, Task>? NotificationReceived;

    /// <summary><see langword="true"/> while a LISTEN connection is open and actively waiting.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>When the current (or most recent) connection was established.</summary>
    public DateTimeOffset? LastConnectedAt { get; private set; }

    /// <summary>When the most recent notification was received, across all connections.</summary>
    public DateTimeOffset? LastNotificationAt { get; private set; }

    /// <summary>
    /// Runs the connect/listen/reconnect loop until <paramref name="cancellationToken"/> is
    /// cancelled. Intended to be run as the body of a background hosted service.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

                // ConnectAsync returning is the actual success signal: unlike the combined
                // connect-and-listen method this replaced, ListenAsync below always ends by
                // throwing (cancellation or a dropped connection), so this is the only point that
                // can ever reach a reset - putting it after the listen loop instead, as before,
                // made it dead code and left the backoff climbing forever after the first failure.
                attempt = 0;

                await ListenAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = reconnectPolicy.GetDelay(attempt);
                if (delay is null)
                {
                    logger.LogCritical(ex, "PostgreSQL notification listener giving up after {Attempt} failed attempts", attempt);
                    throw;
                }

                logger.LogWarning(
                    ex, "PostgreSQL notification listener lost connection (attempt {Attempt}); reconnecting in {DelayMilliseconds}ms",
                    attempt, delay.Value.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Opens the dedicated LISTEN connection and issues every <c>LISTEN</c> command. Returns only
    /// on success; on any failure the partially-opened connection is disposed and the exception
    /// propagates, so callers never have to distinguish "returned a connection" from "failed".
    /// </summary>
    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        // Read per connection attempt, not captured once: the hosted service resolves both while
        // starting, which happens after this instance was constructed.
        var channels = state.Channels;

        // When a template is present, its connection string omits the password on purpose (see
        // NotificationMappingBuilder.UseConnection) - CloneWith still authenticates correctly
        // because it clones the template's actual bound credentials, not its displayed string.
        var connection = state.ListeningConnectionTemplate is { } template
            ? template.CloneWith(state.ConnectionString)
            : new NpgsqlConnection(state.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _connection = connection;

            connection.Notification += OnNotification;

            foreach (var channel in channels)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"LISTEN {QuoteChannel(channel)};";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            LastConnectedAt = DateTimeOffset.UtcNow;
            IsConnected = true;
            logger.LogInformation("PostgreSQL notification listener connected, listening on {ChannelCount} channel(s)", channels.Count);

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Waits for notifications on <paramref name="connection"/> until it is cancelled or the
    /// connection drops. Always ends by throwing - a clean stop surfaces as
    /// <see cref="OperationCanceledException"/>, everything else as whatever <c>WaitAsync</c> threw.
    /// </summary>
    private async Task ListenAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            IsConnected = false;
            connection.Notification -= OnNotification;
            _connection = null;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        LastNotificationAt = DateTimeOffset.UtcNow;

        var handler = NotificationReceived;
        if (handler is null)
        {
            return;
        }

        // Npgsql invokes this handler synchronously from within WaitAsync's processing; fire the
        // (potentially slow, user-owned) dispatch pipeline without blocking the listener's read loop.
        // Tracked via _inFlightDispatchCount rather than the returned Task (still discarded here on
        // purpose): DrainInFlightDispatchesAsync needs to know when the LAST one finishes, not
        // observe any individual one.
        Interlocked.Increment(ref _inFlightDispatchCount);
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.Invoke(args.Channel, args.Payload).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref _inFlightDispatchCount) == 0)
                {
                    _drainTcs?.TrySetResult();
                }
            }
        });
    }

    /// <summary>
    /// Waits for every dispatch already in flight to finish, up to <paramref name="timeout"/>.
    /// Intended for a graceful shutdown, after new notifications have stopped arriving (the caller
    /// has unsubscribed from <see cref="NotificationReceived"/> and stopped <see cref="RunAsync"/>):
    /// dispatches started before that point are otherwise pure fire-and-forget, so a host could
    /// dispose the DI container - and everything a dispatch's scope depends on - while one is still
    /// running.
    /// </summary>
    public async Task DrainInFlightDispatchesAsync(TimeSpan timeout)
    {
        if (Volatile.Read(ref _inFlightDispatchCount) == 0)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drainTcs = tcs;

        // Re-check after publishing the TCS: a dispatch that reached zero between the read above and
        // this assignment would have decremented past _drainTcs being set, and so never signaled it.
        if (Volatile.Read(ref _inFlightDispatchCount) == 0)
        {
            tcs.TrySetResult();
        }

        await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    private static string QuoteChannel(string channel) => "\"" + channel.Replace("\"", "\"\"") + "\"";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            connection.Notification -= OnNotification;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
