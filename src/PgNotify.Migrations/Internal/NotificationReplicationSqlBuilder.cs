using Microsoft.EntityFrameworkCore.Storage;
using PgNotify.Model;

namespace PgNotify.Migrations.Internal;

/// <summary>
/// Builds the idempotent DDL for <see cref="NotificationDeliveryMode.LogicalReplication"/>: a
/// shared publication per <see cref="NotificationEntityConfiguration.NamePrefix"/> scope, that
/// table's membership in it, its <c>REPLICA IDENTITY</c>, and a replication slot per
/// <see cref="NotificationEntityConfiguration.ReplicationConsumerGroup"/>. No trigger is involved
/// at all for an entity on this delivery mode — see
/// <c>docs/plans/logical-replication-delivery.md</c>.
/// </summary>
/// <remarks>
/// Every statement is written to be safe to re-run, the same way
/// <see cref="NotificationTriggerSqlBuilder"/>'s <c>CREATE OR REPLACE FUNCTION</c> /
/// <c>DROP TRIGGER IF EXISTS</c> pair is: unlike a trigger function, PostgreSQL has no
/// <c>CREATE PUBLICATION IF NOT EXISTS</c> or <c>ALTER PUBLICATION ... ADD TABLE IF NOT EXISTS</c>,
/// so idempotency here is a <c>DO</c> block guarded by a catalog lookup instead. This also means
/// each table's statements are self-contained: a publication or slot shared by several entities is
/// "ensured", never assumed already created by another table's migration having run first, because
/// nothing here can see the rest of the model at generation time the way an entity's own
/// configuration is always fully known.
/// </remarks>
internal sealed class NotificationReplicationSqlBuilder(ISqlGenerationHelper sqlGenerationHelper)
{
    private const string DoBlockTag = "$pgnotify_repl$";

    /// <summary>
    /// The statements needed to (re)install <paramref name="config"/>'s replication DDL: the
    /// publication exists, this table is a member, its <c>REPLICA IDENTITY</c> matches
    /// <see cref="NotificationEntityConfiguration.ReplicaIdentityFull"/>, and its consumer group's
    /// slot exists. Empty if no operations are configured, matching
    /// <see cref="NotificationTriggerSqlBuilder.BuildUpsertStatements"/>.
    /// </summary>
    public IReadOnlyList<string> BuildUpsertStatements(NotificationEntityConfiguration config)
    {
        if (config.Operations == NotificationOperations.None)
        {
            return [];
        }

        var publicationName = GetPublicationName(config.NamePrefix);
        var slotName = GetSlotName(config.NamePrefix, config.ReplicationConsumerGroup);

        return
        [
            BuildEnsurePublicationExistsSql(publicationName),
            BuildEnsureTableInPublicationSql(publicationName, config.Schema, config.TableName),
            BuildReplicaIdentitySql(config.Schema, config.TableName, config.ReplicaIdentityFull),
            BuildEnsureSlotExistsSql(slotName),
        ];
    }

    /// <summary>
    /// The statements to remove this table from replication: dropped from the publication, and its
    /// <c>REPLICA IDENTITY</c> reset to <c>DEFAULT</c>. Deliberately does <b>not</b> drop the
    /// publication (other entities under the same <see cref="NotificationEntityConfiguration.NamePrefix"/>
    /// may still be members) or the consumer group's slot — a slot still retains WAL for whatever it
    /// has not yet had confirmed, so dropping it destructively without knowing that is not a
    /// decision this builder makes silently; see
    /// <c>Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions.FindOrphanedNotificationReplicationSlots</c>
    /// for the read-only visibility this leaves in its place.
    /// </summary>
    public IReadOnlyList<string> BuildRemovalStatements(string? schema, string tableName, string namePrefix)
    {
        var publicationName = GetPublicationName(namePrefix);
        return
        [
            BuildEnsureTableNotInPublicationSql(publicationName, schema, tableName),
            BuildReplicaIdentitySql(schema, tableName, replicaIdentityFull: false),
        ];
    }

    private string BuildEnsurePublicationExistsSql(string publicationName)
    {
        var delimited = sqlGenerationHelper.DelimitIdentifier(publicationName);
        return $"""
            DO {DoBlockTag}
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = '{Escape(publicationName)}') THEN
                    EXECUTE 'CREATE PUBLICATION {Escape(delimited)}';
                END IF;
            END
            {DoBlockTag}
            """;
    }

    private string BuildEnsureTableInPublicationSql(string publicationName, string? schema, string tableName)
    {
        var delimitedPub = sqlGenerationHelper.DelimitIdentifier(publicationName);
        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(tableName, schema);
        return $"""
            DO {DoBlockTag}
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_publication_rel pr
                    JOIN pg_publication p ON p.oid = pr.prpubid
                    WHERE p.pubname = '{Escape(publicationName)}' AND pr.prrelid = to_regclass('{Escape(delimitedTable)}')
                ) THEN
                    EXECUTE 'ALTER PUBLICATION {Escape(delimitedPub)} ADD TABLE {Escape(delimitedTable)}';
                END IF;
            END
            {DoBlockTag}
            """;
    }

    private string BuildEnsureTableNotInPublicationSql(string publicationName, string? schema, string tableName)
    {
        var delimitedPub = sqlGenerationHelper.DelimitIdentifier(publicationName);
        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(tableName, schema);
        return $"""
            DO {DoBlockTag}
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM pg_publication_rel pr
                    JOIN pg_publication p ON p.oid = pr.prpubid
                    WHERE p.pubname = '{Escape(publicationName)}' AND pr.prrelid = to_regclass('{Escape(delimitedTable)}')
                ) THEN
                    EXECUTE 'ALTER PUBLICATION {Escape(delimitedPub)} DROP TABLE {Escape(delimitedTable)}';
                END IF;
            END
            {DoBlockTag}
            """;
    }

    /// <summary>
    /// <c>ALTER TABLE ... REPLICA IDENTITY</c> needs no existence guard: re-setting the same value
    /// is a no-op, and PostgreSQL raises nothing for it, unlike <c>CREATE PUBLICATION</c>/
    /// <c>ALTER PUBLICATION ... ADD TABLE</c> on an already-existing publication/membership.
    /// </summary>
    private string BuildReplicaIdentitySql(string? schema, string tableName, bool replicaIdentityFull)
    {
        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(tableName, schema);
        return $"ALTER TABLE {delimitedTable} REPLICA IDENTITY {(replicaIdentityFull ? "FULL" : "DEFAULT")}";
    }

    private string BuildEnsureSlotExistsSql(string slotName) => $"""
        DO {DoBlockTag}
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = '{Escape(slotName)}') THEN
                PERFORM pg_create_logical_replication_slot('{Escape(slotName)}', 'pgoutput');
            END IF;
        END
        {DoBlockTag}
        """;

    /// <summary>
    /// Exposed internally so orphan-slot detection can compute the exact same names to check what's
    /// already deployed against what the current model still asks for, mirroring
    /// <see cref="NotificationTriggerSqlBuilder.GetNames"/>.
    /// </summary>
    internal static string GetPublicationName(string namePrefix) =>
        PostgresIdentifier.EnsureWithinLength($"{namePrefix}pgnotify_pub");

    internal static string GetSlotName(string namePrefix, string consumerGroup) =>
        PostgresIdentifier.EnsureWithinLength($"{namePrefix}pgnotify_{consumerGroup}");

    private static string Escape(string value) => value.Replace("'", "''");
}
