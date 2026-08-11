namespace PgNotify;

/// <summary>
/// Contributes <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured entities — and
/// optionally the connection string — while the replication listener is starting. The replication
/// counterpart to <c>INotificationMappingSource</c>; implement this to derive what to stream from
/// something the container only knows about later, such as a <c>DbContext</c>'s model (see
/// <c>PgNotify.Runtime.EFCore</c>'s <c>AddReplicationMappingFromDbContexts()</c>).
/// </summary>
public interface IReplicationMappingSource
{
    /// <summary>Adds this source's entities to <paramref name="builder"/>.</summary>
    Task ContributeAsync(ReplicationMappingBuilder builder, CancellationToken cancellationToken);
}
