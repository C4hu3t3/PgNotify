using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify.Model;

/// <summary>
/// The fully resolved, immutable notification configuration for one entity. Computed once by
/// <c>PgNotify.EFCore</c> from either the <see cref="NotifyChangesAttribute"/> or the
/// <c>HasDatabaseNotifications()</c> fluent API, and consumed by both
/// <c>PgNotify.Migrations</c> (to generate trigger SQL) and, where useful, the source
/// generator. This is the single source of truth both subsystems agree on — neither duplicates
/// the interpretation of raw annotations.
/// </summary>
public sealed record NotificationEntityConfiguration
{
    /// <summary>The CLR entity type's simple name (e.g. <c>User</c>).</summary>
    public required string EntityDisplayName { get; init; }

    /// <summary>The mapped schema, or <see langword="null"/> for the default schema.</summary>
    public string? Schema { get; init; }

    /// <summary>The mapped table name (e.g. <c>users</c>).</summary>
    public required string TableName { get; init; }

    /// <summary>The operations that raise a notification.</summary>
    public required NotificationOperations Operations { get; init; }

    /// <summary>The primary key column names, in key order. Never empty: notifications require a primary key.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>
    /// The column names to watch for <c>OnUpdate</c>. Empty means "any mapped column change"
    /// (an unfiltered <c>UPDATE</c> trigger) — unless <see cref="UnconditionalUpdate"/> is set,
    /// which means no column is compared at all.
    /// </summary>
    public IReadOnlyList<string> WatchedUpdateColumns { get; init; } = [];

    /// <summary>
    /// When <see langword="true"/>, the generated trigger raises an update notification for every
    /// <c>UPDATE</c> statement that touches the row, without comparing any column with
    /// <c>IS DISTINCT FROM</c> first — including a no-op update that changes no value. The fluent
    /// API's <c>OnUpdate(false)</c>; defaults to <see langword="false"/>, which is every other
    /// <c>OnUpdate</c> shape (bare, or with an explicit property selector).
    /// </summary>
    public bool UnconditionalUpdate { get; init; }

    /// <summary>The strategy used to compute the channel name, unless overridden by <see cref="ChannelNameOverride"/>.</summary>
    public required INotificationChannelNamingStrategy ChannelStrategy { get; init; }

    /// <summary>
    /// An explicit channel name that overrides <see cref="ChannelStrategy"/> for every operation,
    /// set via the fluent API's <c>WithChannelName(...)</c>.
    /// </summary>
    public string? ChannelNameOverride { get; init; }

    /// <summary>
    /// The columns selected by <c>WithPayload(x =&gt; new { ... })</c>, in the order written. Empty
    /// unless <see cref="PayloadBuilder"/> is a <see cref="ProjectedNotificationPayloadBuilder"/>.
    /// </summary>
    public IReadOnlyList<NotificationPayloadColumn> PayloadColumns { get; init; } = [];

    /// <summary>The payload builder describing the JSON shape the trigger should emit.</summary>
    public required INotificationPayloadBuilder PayloadBuilder { get; init; }

    /// <summary>
    /// What the trigger does when the payload would exceed <c>pg_notify</c>'s 7999-byte limit.
    /// Defaults to <see cref="NotificationPayloadOverflow.Truncate"/>, because the alternative
    /// aborts the write that produced the row.
    /// </summary>
    public NotificationPayloadOverflow PayloadOverflow { get; init; } = NotificationPayloadOverflow.Truncate;

    /// <summary>
    /// Prepended to the generated trigger/function names (<c>{NamePrefix}trg_{table}_notify</c> /
    /// <c>{NamePrefix}fn_{schema_}{table}_notify</c>), so generated objects can be made
    /// unambiguous and collision-free against names you already use — most useful when this
    /// library doesn't fully own the database (see <c>EnsureNotificationTriggersAsync</c>).
    /// Empty by default, preserving the original unprefixed names.
    /// </summary>
    public string NamePrefix { get; init; } = "";

    /// <summary>
    /// How this entity's changes reach a listener. <see cref="NotificationDeliveryMode.Notify"/>
    /// (the default) generates a trigger calling <c>pg_notify</c>;
    /// <see cref="NotificationDeliveryMode.LogicalReplication"/> generates a publication/slot
    /// instead, and no trigger at all — see <c>docs/plans/logical-replication-delivery.md</c>.
    /// </summary>
    public NotificationDeliveryMode DeliveryMode { get; init; } = NotificationDeliveryMode.Notify;

    /// <summary>
    /// Only meaningful under <see cref="NotificationDeliveryMode.LogicalReplication"/>: sets the
    /// table's <c>REPLICA IDENTITY</c> to <c>FULL</c> so an <c>UPDATE</c>'s old column values are
    /// available to the replication stream. Required for <see cref="WatchedUpdateColumns"/> to be
    /// enforceable under this delivery mode — without it there is no old row to compare against.
    /// Increases WAL volume; off by default.
    /// </summary>
    public bool ReplicaIdentityFull { get; init; }

    /// <summary>
    /// Only meaningful under <see cref="NotificationDeliveryMode.LogicalReplication"/>: names the
    /// replication slot's consumer group. A slot supports exactly one active stream, so distinct
    /// consumer groups reading the same tables need distinct slot names — this is how independent
    /// listener processes each see every change, the durable equivalent of NOTIFY's fan-out.
    /// Defaults to <c>"default"</c> when unset.
    /// </summary>
    public string ReplicationConsumerGroup { get; init; } = "default";

    /// <summary>Resolves the channel name for a specific operation, honoring <see cref="ChannelNameOverride"/> first.</summary>
    public string GetChannelName(NotificationOperation operation)
    {
        if (ChannelNameOverride is { Length: > 0 } overrideName)
        {
            return PostgresIdentifier.EnsureWithinLength(overrideName);
        }

        var context = new NotificationChannelNamingContext(EntityDisplayName, Schema, TableName, operation);
        return ChannelStrategy.GetChannelName(context);
    }

    /// <summary>Builds the payload field list for this entity via <see cref="PayloadBuilder"/>.</summary>
    public IReadOnlyList<NotificationPayloadField> BuildPayloadFields()
    {
        var context = new NotificationPayloadBuilderContext(
            EntityDisplayName, Schema, TableName, KeyColumns, WatchedUpdateColumns, PayloadColumns);
        return PayloadBuilder.BuildFields(context);
    }
}
