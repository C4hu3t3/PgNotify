using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// Runs one connect/stream/reconnect loop per distinct <c>(NamePrefix, ReplicationConsumerGroup)</c>
/// slot for the lifetime of the host, decoding each committed change into the same JSON shape the
/// trigger/NOTIFY path would have produced (<see cref="NotificationPayloadJsonMaterializer"/>) and
/// publishing it through <see cref="INotificationPublisher"/> — the same dispatch pipeline
/// <c>PostgresNotificationHostedService</c> uses for NOTIFY-delivered notifications.
/// </summary>
/// <remarks>
/// Confirms a transaction's LSN only after every change in it has been dispatched successfully — a
/// dispatch failure (including one a handler raises, if it survives whatever
/// <c>RetryNotificationMiddleware</c> would otherwise absorb) propagates out of this loop and
/// becomes a connection-level failure, so the whole transaction replays on reconnect rather than
/// silently losing it. This is a deliberate difference from <c>PostgresNotificationHostedService</c>,
/// which swallows and logs a dispatch exception instead: NOTIFY delivery has no redelivery
/// mechanism to fall back on, so swallowing is the only option there, whereas here it would throw
/// away the one property — at-least-once — this delivery mode exists to provide. A handler that
/// fails every time therefore turns into a permanent reconnect loop until
/// <see cref="PostgresLogicalReplicationOptions.ReconnectPolicy"/> gives up, the same "eventually a
/// hard failure" shape unrecoverable errors already have elsewhere in this codebase.
/// </remarks>
internal sealed class LogicalReplicationHostedService(
    IServiceScopeFactory scopeFactory,
    INotificationPublisher publisher,
    INotificationPayloadDeserializer payloadDeserializer,
    PostgresLogicalReplicationOptions options,
    ILogger<LogicalReplicationHostedService> logger) : IHostedService
{
    private CancellationTokenSource? _stoppingCts;
    private List<Task>? _slotTasks;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = new ReplicationMappingBuilder();
        using (var scope = scopeFactory.CreateScope())
        {
            foreach (var source in scope.ServiceProvider.GetServices<IReplicationMappingSource>())
            {
                await source.ContributeAsync(builder, cancellationToken).ConfigureAwait(false);
            }
        }

        if (builder.Mappings.Count == 0)
        {
            logger.LogInformation(
                "No LogicalReplication-configured entities found; the replication listener has nothing to stream and will not connect.");
            return;
        }

        var connectionString = ResolveConnectionString(builder);

        // Registered under every distinct channel the entity's configured operations would use,
        // mirroring GetNotificationChannels()'s per-operation expansion for the topic strategy, so
        // routing is identical to whatever the NOTIFY path would have registered for the same
        // configuration.
        foreach (var mapping in builder.Mappings)
        {
            foreach (var channel in mapping.Config.Operations.Expand().Select(mapping.Config.GetChannelName).Distinct(StringComparer.Ordinal))
            {
                publisher.RegisterEntity(channel, mapping.Config.EntityDisplayName, mapping.EntityType);
            }
        }

        _stoppingCts = new CancellationTokenSource();
        _slotTasks = [];

        var bySlot = builder.Mappings.GroupBy(m => (m.Config.NamePrefix, m.Config.ReplicationConsumerGroup)).ToArray();
        foreach (var group in bySlot)
        {
            var (namePrefix, consumerGroup) = group.Key;
            var slotName = NotificationReplicationNames.GetSlotName(namePrefix, consumerGroup);
            var publicationName = NotificationReplicationNames.GetPublicationName(namePrefix);
            var byTable = group.ToDictionary(m => (Schema: m.Config.Schema ?? "public", m.Config.TableName));

            _slotTasks.Add(RunSlotAsync(connectionString, publicationName, slotName, byTable, _stoppingCts.Token));
        }

        logger.LogInformation(
            "PostgreSQL logical replication listener starting {SlotCount} slot(s) for {EntityCount} entit(y/ies)",
            bySlot.Length, builder.Mappings.Count);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null)
        {
            return;
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);

        if (_slotTasks is { Count: > 0 })
        {
            try
            {
                await Task.WhenAll(_slotTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: either our own stopping token or the host's shutdown token fired.
            }
        }
    }

    private string ResolveConnectionString(ReplicationMappingBuilder builder)
    {
        if (options.ConnectionString is { Length: > 0 } configured)
        {
            return NotificationConnectionString.ForReplication(configured);
        }

        return builder.ProposedConnectionStrings.Count switch
        {
            1 => NotificationConnectionString.ForReplication(builder.ProposedConnectionStrings.Single()),
            0 => throw new InvalidOperationException(
                $"No connection string for the logical replication listener: set {nameof(PostgresLogicalReplicationOptions)}." +
                $"{nameof(PostgresLogicalReplicationOptions.ConnectionString)}, or register an " +
                $"{nameof(IReplicationMappingSource)} that supplies one."),
            _ => throw new InvalidOperationException(
                $"{builder.ProposedConnectionStrings.Count} different connection strings were proposed for the logical " +
                $"replication listener. Set {nameof(PostgresLogicalReplicationOptions)}." +
                $"{nameof(PostgresLogicalReplicationOptions.ConnectionString)} explicitly, or narrow the mapping to one source."),
        };
    }

    private async Task RunSlotAsync(
        string connectionString,
        string publicationName,
        string slotName,
        IReadOnlyDictionary<(string Schema, string TableName), ReplicationEntityMapping> byTable,
        CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await StreamSlotAsync(connectionString, publicationName, slotName, byTable, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = options.ReconnectPolicy.GetDelay(attempt);
                if (delay is null)
                {
                    logger.LogCritical(
                        ex, "PostgreSQL logical replication listener for slot {SlotName} giving up after {Attempt} failed attempts",
                        slotName, attempt);
                    throw;
                }

                logger.LogWarning(
                    ex, "PostgreSQL logical replication listener for slot {SlotName} lost connection (attempt {Attempt}); reconnecting in {DelayMilliseconds}ms",
                    slotName, attempt, delay.Value.TotalMilliseconds);

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

    private async Task StreamSlotAsync(
        string connectionString,
        string publicationName,
        string slotName,
        IReadOnlyDictionary<(string Schema, string TableName), ReplicationEntityMapping> byTable,
        CancellationToken cancellationToken)
    {
        await using var connection = new LogicalReplicationConnection(connectionString);
        await connection.Open(cancellationToken).ConfigureAwait(false);

        var slot = new PgOutputReplicationSlot(slotName);
        var replicationOptions = new PgOutputReplicationOptions(publicationName, PgOutputProtocolVersion.V1);
        var pending = new List<PendingChange>();

        logger.LogInformation("PostgreSQL logical replication listener connected for slot {SlotName}", slotName);

        // No resumeFromLsn: the slot's own confirmed_flush_lsn already IS the durable resume point
        // (proven in the Phase 0 spike), so there is no client-side position to track or pass here.
        await foreach (var message in connection.StartReplication(slot, replicationOptions, cancellationToken))
        {
            switch (message)
            {
                // RelationMessage needs no handling of its own: InsertMessage/UpdateMessage/
                // DeleteMessage each carry their own already-resolved Relation directly.
                case InsertMessage insert:
                    await TryQueueAsync(pending, byTable, insert.Relation, NotificationOperation.Insert,
                        current: insert.NewRow, previous: null, message.ServerClock, cancellationToken).ConfigureAwait(false);
                    break;

                case FullUpdateMessage fullUpdate:
                    await TryQueueAsync(pending, byTable, fullUpdate.Relation, NotificationOperation.Update,
                        current: fullUpdate.NewRow, previous: fullUpdate.OldRow, message.ServerClock, cancellationToken).ConfigureAwait(false);
                    break;

                case UpdateMessage update:
                    // DefaultUpdateMessage / IndexUpdateMessage: no old row available (default
                    // REPLICA IDENTITY carries only the new row) -- the validation convention
                    // already refuses a filtered OnUpdate() without ReplicaIdentityFull, so an
                    // unfiltered one is the only shape that ever reaches here.
                    await TryQueueAsync(pending, byTable, update.Relation, NotificationOperation.Update,
                        current: update.NewRow, previous: null, message.ServerClock, cancellationToken).ConfigureAwait(false);
                    break;

                case FullDeleteMessage fullDelete:
                    await TryQueueAsync(pending, byTable, fullDelete.Relation, NotificationOperation.Delete,
                        current: fullDelete.OldRow, previous: null, message.ServerClock, cancellationToken).ConfigureAwait(false);
                    break;

                case KeyDeleteMessage keyDelete:
                    // Default REPLICA IDENTITY: only the primary key columns are available for a
                    // deleted row -- always enough for NotificationEntityConfiguration.KeyColumns,
                    // never enough for a payload that also projects other columns.
                    await TryQueueAsync(pending, byTable, keyDelete.Relation, NotificationOperation.Delete,
                        current: keyDelete.Key, previous: null, message.ServerClock, cancellationToken).ConfigureAwait(false);
                    break;

                case TruncateMessage:
                    logger.LogWarning(
                        "Received TRUNCATE on a LogicalReplication-configured table on slot {SlotName}; TRUNCATE has no " +
                        "notification equivalent and was skipped.", slotName);
                    break;

                case CommitMessage:
                    // Dispatch everything this transaction touched before confirming: confirming a
                    // mid-transaction LSN does not move the server's resumption point at all
                    // (logical decoding is transaction-granular, proven in the Phase 0 spike), and
                    // confirming before a dispatch that later fails would silently lose it.
                    foreach (var change in pending)
                    {
                        await DispatchAsync(change, cancellationToken).ConfigureAwait(false);
                    }

                    pending.Clear();

                    connection.SetReplicationStatus(message.WalEnd);
                    await connection.SendStatusUpdate(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task TryQueueAsync(
        List<PendingChange> pending,
        IReadOnlyDictionary<(string Schema, string TableName), ReplicationEntityMapping> byTable,
        RelationMessage relation,
        NotificationOperation operation,
        ReplicationTuple current,
        ReplicationTuple? previous,
        DateTime serverClock,
        CancellationToken cancellationToken)
    {
        if (!byTable.TryGetValue((relation.Namespace, relation.RelationName), out var mapping))
        {
            return;
        }

        // The pgoutput wire format puts the old tuple before the new one on an UPDATE message
        // (FullUpdateMessage.OldRow then .NewRow) -- each ReplicationTuple is a forward-only,
        // single-pass reader directly over that stream, so decoding them out of that order corrupts
        // the read position for whatever comes next ("The row is already been read"). Old first,
        // when present, matches the wire order regardless of which one the caller names first.
        var previousValues = previous is null ? null : await DecodeAsync(previous, relation, cancellationToken).ConfigureAwait(false);
        var currentValues = await DecodeAsync(current, relation, cancellationToken).ConfigureAwait(false);

        pending.Add(new PendingChange(mapping, operation, currentValues, previousValues, serverClock));
    }

    private static async Task<Dictionary<string, object?>> DecodeAsync(
        ReplicationTuple tuple, RelationMessage relation, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>();
        var index = 0;

        await foreach (var value in tuple.WithCancellation(cancellationToken))
        {
            var columnName = relation.Columns[index].ColumnName;
            values[columnName] = value.IsDBNull ? null : await value.Get<object>(cancellationToken).ConfigureAwait(false);
            index++;
        }

        return values;
    }

    private async Task DispatchAsync(PendingChange change, CancellationToken cancellationToken)
    {
        var channel = change.Mapping.Config.GetChannelName(change.Operation);
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(change.ServerClock, DateTimeKind.Utc));

        var json = NotificationPayloadJsonMaterializer.Materialize(
            change.Mapping.Config, change.Operation, change.CurrentValues, change.PreviousValues, timestamp);

        var envelope = payloadDeserializer.Deserialize(channel, json);
        await publisher.PublishAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private sealed record PendingChange(
        ReplicationEntityMapping Mapping,
        NotificationOperation Operation,
        IReadOnlyDictionary<string, object?> CurrentValues,
        IReadOnlyDictionary<string, object?>? PreviousValues,
        DateTime ServerClock);
}
