using System.Data;
using System.Data.Common;

namespace PgNotify.Migrations.Internal;

/// <summary>One function/trigger pair found deployed, before it's checked against the current model's candidates.</summary>
internal readonly record struct DeployedNotificationTrigger(string Schema, string TableName, string FunctionName, string TriggerName);

/// <summary>
/// Finds every function/trigger pair this library could plausibly have created anywhere in the
/// database — not just for tables the current model still asks for — by looking for the two marks
/// <see cref="NotificationTriggerSqlBuilder"/> always leaves behind together: a
/// <c>COMMENT ON FUNCTION</c> holding a 64-hex-character SHA-256 digest (see
/// <c>NotificationFingerprint.ComputeHash</c>) and a non-internal trigger executing that function
/// <c>AFTER INSERT/UPDATE/DELETE</c>. Neither mark alone is unique to this library — a hand-written
/// comment could coincidentally look like 64 hex characters, and no other assumption is made about
/// what a trigger's name looks like — so both are required together, the same pair
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions"/> always deploys
/// as a unit.
/// </summary>
internal static class OrphanedNotificationTriggerReader
{
    private const string Sql = """
        SELECT DISTINCT n.nspname AS schema_name, c.relname AS table_name, p.proname AS function_name, t.tgname AS trigger_name
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        JOIN pg_trigger t ON t.tgfoid = p.oid AND NOT t.tgisinternal
        JOIN pg_class c ON c.oid = t.tgrelid
        WHERE obj_description(p.oid, 'pg_proc') ~ '^[0-9a-f]{64}$'
        """;

    public static IReadOnlyList<DeployedNotificationTrigger> Read(DbConnection connection)
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

    public static async Task<IReadOnlyList<DeployedNotificationTrigger>> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
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

    private static List<DeployedNotificationTrigger> ReadResults(DbDataReader reader)
    {
        var results = new List<DeployedNotificationTrigger>();
        while (reader.Read())
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    private static async Task<List<DeployedNotificationTrigger>> ReadResultsAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var results = new List<DeployedNotificationTrigger>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    private static DeployedNotificationTrigger ReadRow(DbDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
}
