namespace PgNotify;

/// <summary>
/// How a notified entity's changes reach a listener. Configured per entity via
/// <c>WithDelivery(...)</c> or <c>[NotifyChanges(Delivery = ...)]</c>.
/// </summary>
public enum NotificationDeliveryMode
{
    /// <summary>
    /// A trigger calls <c>pg_notify</c> on write. Fire-and-forget: a notification fired while no
    /// listener is connected is lost, with no replay. This is the default, and the only mode
    /// available before this option existed.
    /// </summary>
    Notify = 0,

    /// <summary>
    /// Changes are read from PostgreSQL's write-ahead log through a logical replication slot
    /// instead of a trigger. A slot retains WAL until the listener confirms it has processed a
    /// position, so a disconnected listener loses nothing and resumes exactly where it left off —
    /// at-least-once delivery, at the cost of operational prerequisites <c>Notify</c> does not
    /// have (<c>wal_level = logical</c>, a role with the <c>REPLICATION</c> attribute). An
    /// explicit, per-entity opt-in: nothing is enrolled in this mode by default.
    /// </summary>
    LogicalReplication,
}
