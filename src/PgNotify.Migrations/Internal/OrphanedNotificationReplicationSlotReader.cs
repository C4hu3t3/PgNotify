using System.Data;
using System.Data.Common;

namespace PgNotify.Migrations.Internal;

/// <summary>One replication slot found deployed, before it's checked against the current model's candidates.</summary>
internal readonly record struct DeployedNotificationReplicationSlot(string SlotName, bool Active, string WalStatus);

/// <summary>
/// Finds every <c>pgoutput</c> replication slot whose name matches this library's naming
/// convention (<see cref="NotificationReplicationSqlBuilder.GetSlotName"/> always produces
/// <c>{NamePrefix}pgnotify_{ConsumerGroup}</c>) — a naming-pattern match, not a hash-verified mark
/// the way <see cref="OrphanedNotificationTriggerReader"/> uses <c>COMMENT ON FUNCTION</c>, because
/// a replication slot has nothing equivalent to attach one to.
/// </summary>
internal static class OrphanedNotificationReplicationSlotReader
{
    private const string Sql = """
        SELECT slot_name, active, wal_status
        FROM pg_replication_slots
        WHERE plugin = 'pgoutput' AND slot_name LIKE '%pgnotify\_%' ESCAPE '\'
        """;

    public static IReadOnlyList<DeployedNotificationReplicationSlot> Read(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            using var reader = command.ExecuteReader();
            return ReadResults(reader);
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

    public static async Task<IReadOnlyList<DeployedNotificationReplicationSlot>> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await ReadResultsAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private static List<DeployedNotificationReplicationSlot> ReadResults(DbDataReader reader)
    {
        var results = new List<DeployedNotificationReplicationSlot>();
        while (reader.Read())
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    private static async Task<List<DeployedNotificationReplicationSlot>> ReadResultsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var results = new List<DeployedNotificationReplicationSlot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    private static DeployedNotificationReplicationSlot ReadRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetBoolean(1), reader.GetString(2));
}
