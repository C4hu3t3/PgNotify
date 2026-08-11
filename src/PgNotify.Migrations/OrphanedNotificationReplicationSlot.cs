namespace PgNotify.Migrations;

/// <summary>
/// A replication slot found deployed that matches this library's naming convention
/// (<c>{NamePrefix}pgnotify_{ConsumerGroup}</c>) but that no entity in the current
/// <see cref="NotificationDeliveryMode.LogicalReplication"/> configuration asks for any more —
/// most often because the entity's delivery mode, consumer group, or name prefix changed, or its
/// notification configuration was removed entirely, since the slot was created. Returned by
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions.FindOrphanedNotificationReplicationSlots"/>/
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions.FindOrphanedNotificationReplicationSlotsAsync"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="OrphanedNotificationTrigger"/>, identification here is name-pattern matching
/// only, not a hash-verified mark: a replication slot has no equivalent of <c>COMMENT ON
/// FUNCTION</c> to carry an out-of-band fingerprint, so a slot this library did not create that
/// happens to match the naming convention would also be reported. Deliberately has no
/// <c>DropStatements</c> and no <c>Remove...</c> counterpart, unlike the trigger case: dropping a
/// slot permanently discards its confirmed replication position and, done on the wrong slot,
/// silently discards WAL another consumer hasn't read yet — a decision this library does not make
/// for you. <see cref="WalStatus"/> is surfaced specifically so you can tell how much is actually at
/// stake before deciding: <c>reserved</c>/<c>extended</c> means PostgreSQL is retaining WAL for this
/// slot right now.
/// </remarks>
/// <param name="SlotName">The deployed slot's name.</param>
/// <param name="Active">Whether a consumer currently holds this slot's replication stream open.</param>
/// <param name="WalStatus">
/// PostgreSQL's <c>pg_replication_slots.wal_status</c> for this slot (e.g. <c>reserved</c>,
/// <c>extended</c>, <c>lost</c>) — how much WAL retention this orphaned slot is actually costing.
/// </param>
public sealed record OrphanedNotificationReplicationSlot(string SlotName, bool Active, string WalStatus);
