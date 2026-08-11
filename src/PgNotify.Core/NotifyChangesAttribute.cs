using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify;

/// <summary>
/// Marks an entity class as a source of PostgreSQL LISTEN/NOTIFY database notifications.
/// Discovered by the <c>PgNotify.EFCore</c> model-building convention and, at compile time,
/// by the <c>PgNotify.SourceGenerators</c> incremental generator.
/// </summary>
/// <remarks>
/// Everything configurable through <c>modelBuilder.Entity&lt;T&gt;().HasDatabaseNotifications(...)</c>
/// is configurable here, and the two write the same annotations through the same code path — so
/// either style (or both, though see analyzer diagnostic PGN001) can be used, and moving an entity
/// from one to the other changes nothing about what is generated.
/// </remarks>
/// <remarks>
/// Creates a new <see cref="NotifyChangesAttribute"/>.
/// </remarks>
/// <param name="operations">
/// The operations to notify on. Defaults to <see cref="NotificationOperations.All"/>.
/// </param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class NotifyChangesAttribute(NotificationOperations operations = NotificationOperations.All) : Attribute
{
    /// <summary>
    /// The insert/update/delete operations that should raise a notification.
    /// </summary>
    public NotificationOperations Operations { get; } = operations;

    /// <summary>
    /// Prepended to the generated trigger/function names, e.g. <c>[NotifyChanges(NamePrefix =
    /// "myapp_")]</c> produces <c>myapp_trg_{table}_notify</c> / <c>myapp_fn_{table}_notify</c>
    /// instead of the default <c>trg_{table}_notify</c> / <c>fn_{table}_notify</c> — use this to
    /// avoid colliding with functions/triggers you don't own. Falls back to the model-wide
    /// default set via <c>modelBuilder.HasNotificationNamePrefix(...)</c>, or no prefix, when unset.
    /// </summary>
    public string? NamePrefix { get; set; }

    /// <summary>
    /// Restricts update notifications to changes of these properties, compared with
    /// <c>IS DISTINCT FROM</c> in the trigger — the attribute form of
    /// <c>OnUpdate(x =&gt; new { x.Name, x.Email })</c>. Write them with <c>nameof</c>: an unknown or
    /// non-scalar name fails when the model is built. Empty (the default) watches every mapped column.
    /// </summary>
    public string[]? WatchedProperties { get; set; }

    /// <summary>
    /// The attribute form of <c>OnUpdate(false)</c>. Set to <see langword="false"/> to raise an
    /// update notification for every <c>UPDATE</c> statement that touches the row, without
    /// comparing any column first — including one that changes no value. Defaults to
    /// <see langword="true"/>, which only fires when a mapped column actually changed. Setting
    /// this to <see langword="false"/> together with <see cref="WatchedProperties"/> fails when
    /// the model is built: there is nothing to compare once comparison is turned off.
    /// </summary>
    public bool CompareColumnsOnUpdate { get; set; } = true;

    /// <summary>
    /// Which built-in payload shape the trigger emits. Defaults to
    /// <see cref="NotificationPayloadKind.Minimal"/>, as the fluent API does.
    /// </summary>
    public NotificationPayloadKind Payload { get; set; } = NotificationPayloadKind.Minimal;

    /// <summary>
    /// Projects exactly these properties into the payload, alongside the entity name, the operation
    /// and the row's key — the attribute form of <c>WithPayload(x =&gt; new { x.Status, x.Total })</c>.
    /// Setting it overrides <see cref="Payload"/>. Write the names with <c>nameof</c>.
    /// </summary>
    public string[]? PayloadProperties { get; set; }

    /// <summary>
    /// A custom <c>INotificationPayloadBuilder</c> type, which must have a public parameterless
    /// constructor — the attribute form of <c>WithPayload&lt;TBuilder&gt;()</c>. Setting it overrides
    /// <see cref="Payload"/> and <see cref="PayloadProperties"/>.
    /// </summary>
    public Type? PayloadBuilder { get; set; }

    /// <summary>
    /// Which built-in channel-naming strategy names this entity's channels. Defaults to
    /// <see cref="NotificationChannelStrategy.PerEntity"/>.
    /// </summary>
    public NotificationChannelStrategy ChannelStrategy { get; set; } = NotificationChannelStrategy.PerEntity;

    /// <summary>
    /// The strategy's parameter: the shared channel name for
    /// <see cref="NotificationChannelStrategy.Single"/>, the separator for
    /// <see cref="NotificationChannelStrategy.Topic"/>. Each strategy's own default applies when unset.
    /// </summary>
    public string? ChannelArgument { get; set; }

    /// <summary>
    /// A custom <c>INotificationChannelNamingStrategy</c> type, which must have a public
    /// parameterless constructor — the attribute form of <c>WithChannelStrategy&lt;TStrategy&gt;()</c>.
    /// Setting it overrides <see cref="ChannelStrategy"/>.
    /// </summary>
    public Type? ChannelStrategyType { get; set; }

    /// <summary>
    /// Overrides the channel name for every operation, bypassing the strategy entirely — the
    /// attribute form of <c>WithChannelName(...)</c>.
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// What the trigger does when the payload would exceed <c>pg_notify</c>'s 7999-byte limit.
    /// Defaults to <see cref="NotificationPayloadOverflow.Truncate"/>.
    /// </summary>
    public NotificationPayloadOverflow PayloadOverflow { get; set; } = NotificationPayloadOverflow.Truncate;

    /// <summary>
    /// How this entity's changes reach a listener. Defaults to
    /// <see cref="NotificationDeliveryMode.Notify"/> — see
    /// <see cref="NotificationDeliveryMode.LogicalReplication"/> for what changes when set to it,
    /// including its operational prerequisites. The attribute form of <c>WithDelivery(...)</c>.
    /// </summary>
    public NotificationDeliveryMode Delivery { get; set; } = NotificationDeliveryMode.Notify;

    /// <summary>
    /// Only meaningful with <see cref="Delivery"/> set to
    /// <see cref="NotificationDeliveryMode.LogicalReplication"/>: the attribute form of
    /// <c>WithReplicaIdentityFull()</c>.
    /// </summary>
    public bool ReplicaIdentityFull { get; set; }

    /// <summary>
    /// Only meaningful with <see cref="Delivery"/> set to
    /// <see cref="NotificationDeliveryMode.LogicalReplication"/>: the attribute form of
    /// <c>WithDelivery(...)</c>'s <c>consumerGroup</c> parameter. Defaults to <c>"default"</c>
    /// when unset.
    /// </summary>
    public string? ReplicationConsumerGroup { get; set; }
}
