using PgNotify.Model;

namespace PgNotify;

/// <summary>
/// One <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured entity, as contributed
/// by an <see cref="IReplicationMappingSource"/>. Pairs the same
/// <see cref="NotificationEntityConfiguration"/> <c>PgNotify.Migrations</c> generated DDL from with
/// the CLR type handlers are keyed on — the configuration alone can't supply that; it is EF Core
/// metadata's job, same reason <c>NotificationChannelBinding</c> pairs the two on the LISTEN/NOTIFY
/// side.
/// </summary>
/// <param name="Config">The resolved notification configuration.</param>
/// <param name="EntityType">The CLR type whose handlers receive this entity's notifications.</param>
public sealed record ReplicationEntityMapping(NotificationEntityConfiguration Config, Type EntityType);
