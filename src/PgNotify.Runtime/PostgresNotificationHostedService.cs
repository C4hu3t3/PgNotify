using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PgNotify.Dispatch;
using PgNotify.Internal;
using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Runs the notification listener for the lifetime of the host: starts listening on
/// <see cref="IHostedService.StartAsync"/>, and on <see cref="IHostedService.StopAsync"/>
/// cancels the listen loop and closes the connection gracefully (PostgreSQL implicitly
/// <c>UNLISTEN</c>s everything when the connection closes).
/// </summary>
internal sealed class PostgresNotificationHostedService(
    NpgsqlNotificationListener listener,
    NotificationDispatchPipeline pipeline,
    IServiceScopeFactory scopeFactory,
    INotificationPayloadDeserializer payloadDeserializer,
    NotificationRuntimeState state,
    NotificationChannelMap channelMap,
    PostgresNotificationsOptions options,
    ILogger<PostgresNotificationHostedService> logger) : IHostedService
{
    private CancellationTokenSource? _stoppingCts;
    private Task? _runTask;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// No connection string was configured and no <see cref="INotificationMappingSource"/> supplied
    /// one.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ResolveMappingAsync(cancellationToken).ConfigureAwait(false);

        _stoppingCts = new CancellationTokenSource();
        listener.NotificationReceived += OnNotificationReceivedAsync;
        _runTask = listener.RunAsync(_stoppingCts.Token);
    }

    /// <summary>
    /// Runs every <see cref="INotificationMappingSource"/> and settles what to connect to and what
    /// to <c>LISTEN</c> on. Deliberately here rather than at registration: a source derives its
    /// mapping from services (a <c>DbContext</c>, most of all) that may be registered after
    /// <c>AddPostgresNotifications()</c> was called.
    /// </summary>
    private async Task ResolveMappingAsync(CancellationToken cancellationToken)
    {
        var builder = new NotificationMappingBuilder(channelMap);

        using (var scope = scopeFactory.CreateScope())
        {
            foreach (var source in scope.ServiceProvider.GetServices<INotificationMappingSource>())
            {
                await source.ContributeAsync(builder, cancellationToken).ConfigureAwait(false);
            }
        }

        (state.ConnectionString, state.ListeningConnectionTemplate) = ResolveConnection(builder);

        state.Channels = [.. channelMap.Channels];
        channelMap.MarkMappingResolved();

        logger.LogInformation(
            "PostgreSQL notification mapping resolved: {ChannelCount} channel(s)", state.Channels.Count);

        var orphanedHandlerEntities = options.HandlerEntityTypes.Except(channelMap.MappedEntityTypes).ToArray();
        if (orphanedHandlerEntities.Length > 0)
        {
            logger.LogWarning(
                "{Count} entity type(s) have a registered notification handler but no channel mapped to " +
                "them, so their handlers will never run: {EntityTypes}. Declare a channel for each via " +
                "AddNotificationMappingFromDbContexts() or MapChannel<TEntity>(...).",
                orphanedHandlerEntities.Length,
                string.Join(", ", orphanedHandlerEntities.Select(t => t.Name)));
        }
    }

    private (string ConnectionString, NpgsqlConnection? Template) ResolveConnection(NotificationMappingBuilder builder)
    {
        // An explicit options.ConnectionString never gets a template: it is a first-class,
        // DbContext-free path, so there is no source connection to clone real credentials from -
        // the string it names has to carry its own password, same as before this existed.
        if (options.ConnectionString is { Length: > 0 } configured)
        {
            return (NotificationConnectionString.ForListening(configured), null);
        }

        var connectionString = builder.ProposedConnectionStrings.Count switch
        {
            1 => builder.ProposedConnectionStrings.Single(),
            0 => throw new InvalidOperationException(
                $"No connection string for the notification listener: set {nameof(PostgresNotificationsOptions)}." +
                $"{nameof(PostgresNotificationsOptions.ConnectionString)}, or register an " +
                $"{nameof(INotificationMappingSource)} that supplies one."),

            // Silently taking one of them would point the listener at a database nobody chose, and
            // the symptom - notifications from the other one never arriving - looks like anything
            // but a connection string.
            _ => throw new InvalidOperationException(
                $"{builder.ProposedConnectionStrings.Count} different connection strings were proposed for the " +
                $"notification listener. Set {nameof(PostgresNotificationsOptions)}." +
                $"{nameof(PostgresNotificationsOptions.ConnectionString)} explicitly, or narrow the mapping to one source."),
        };

        // Applied unconditionally, even though every proposer already ran its string through this:
        // idempotent, so free insurance against a source that forgot to - and TemplateFor relies on
        // that same idempotency to find the template UseConnection recorded under the once-applied
        // string, by looking it up under the twice-applied one instead.
        var listening = NotificationConnectionString.ForListening(connectionString);
        return (listening, builder.TemplateFor(listening));
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        listener.NotificationReceived -= OnNotificationReceivedAsync;

        if (_stoppingCts is not null)
        {
            await _stoppingCts.CancelAsync().ConfigureAwait(false);
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: either our own stopping token or the host's shutdown token fired.
            }
        }

        // _runTask ending only means the LISTEN connection stopped; every notification it already
        // received dispatched via a fire-and-forget Task.Run (see NpgsqlNotificationListener.
        // OnNotification), so without this, StopAsync could return - and the host could go on to
        // dispose the DI container - while one of those was still running in a scope that no longer
        // has anything valid to resolve.
        await listener.DrainInFlightDispatchesAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await listener.DisposeAsync().ConfigureAwait(false);

        if (state.ListeningConnectionTemplate is { } template)
        {
            await template.DisposeAsync().ConfigureAwait(false);
            state.ListeningConnectionTemplate = null;
        }
    }

    private async Task OnNotificationReceivedAsync(string channel, string payload)
    {
        var cancellationToken = _stoppingCts?.Token ?? CancellationToken.None;

        using var scope = scopeFactory.CreateScope();
        try
        {
            var envelope = payloadDeserializer.Deserialize(channel, payload);
            var context = new NotificationContext
            {
                Envelope = envelope,
                Services = scope.ServiceProvider,
                CancellationToken = cancellationToken,
            };

            await pipeline.InvokeAsync(context).ConfigureAwait(false);
        }
        catch (NotificationPayloadFormatException ex)
        {
            logger.LogWarning(ex, "Received malformed notification payload on channel {Channel}", channel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error dispatching notification on channel {Channel}", channel);
        }
    }
}
