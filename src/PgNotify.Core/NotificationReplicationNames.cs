namespace PgNotify;

/// <summary>
/// The deterministic naming formula for the objects <see cref="NotificationDeliveryMode.LogicalReplication"/>
/// generates: a publication shared by every entity under one <see cref="Model.NotificationEntityConfiguration.NamePrefix"/>
/// scope, and a replication slot per <see cref="Model.NotificationEntityConfiguration.ReplicationConsumerGroup"/>
/// within it. Shared between <c>PgNotify.Migrations</c> (which generates the DDL these names appear
/// in) and <c>PgNotify.Runtime.Replication</c> (which has to compute the exact same names to know
/// what to stream from), so there is exactly one place this formula is written.
/// </summary>
public static class NotificationReplicationNames
{
    /// <summary>The publication name for <paramref name="namePrefix"/>'s scope.</summary>
    public static string GetPublicationName(string namePrefix) =>
        PostgresIdentifier.EnsureWithinLength($"{namePrefix}pgnotify_pub");

    /// <summary>The replication slot name for <paramref name="consumerGroup"/> within <paramref name="namePrefix"/>'s scope.</summary>
    public static string GetSlotName(string namePrefix, string consumerGroup) =>
        PostgresIdentifier.EnsureWithinLength($"{namePrefix}pgnotify_{consumerGroup}");
}
