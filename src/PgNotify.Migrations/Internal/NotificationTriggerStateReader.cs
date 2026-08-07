using System.Data;
using System.Data.Common;

namespace PgNotify.Migrations.Internal;

/// <summary>One candidate table's expected (prefixed, length-truncated) trigger/function names.</summary>
internal readonly record struct NotificationTriggerCandidate(string Schema, string TableName, string FunctionName, string TriggerName);

/// <summary>What's currently deployed for one <see cref="NotificationTriggerCandidate"/>.</summary>
internal readonly record struct NotificationTriggerDeployedState(string? FingerprintHash, bool TriggerExists);

/// <summary>
/// Reads, in a single round trip, whether each candidate's function/trigger already matches what
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions"/> is about to
/// deploy — so it can skip re-running <c>CREATE OR REPLACE FUNCTION</c> /
/// <c>DROP TRIGGER</c>/<c>CREATE TRIGGER</c> for tables where nothing changed. The function's
/// current fingerprint hash comes from <c>COMMENT ON FUNCTION</c> (see
/// <c>NotificationFingerprint.ComputeHash</c>/<c>obj_description</c>); trigger existence is
/// checked separately so a function whose comment still matches but whose trigger was dropped
/// externally is correctly treated as "not deployed", not skipped.
/// </summary>
internal static class NotificationTriggerStateReader
{
    private const string Sql = """
        SELECT x.schema_name, x.function_name,
               obj_description(p.oid, 'pg_proc') AS fingerprint_hash,
               (t.oid IS NOT NULL) AS trigger_exists
        FROM unnest(@schemas, @tableNames, @functionNames, @triggerNames)
            WITH ORDINALITY AS x(schema_name, table_name, function_name, trigger_name, ordinality)
        LEFT JOIN pg_namespace n ON n.nspname = x.schema_name
        LEFT JOIN pg_proc p ON p.pronamespace = n.oid AND p.proname = x.function_name
        LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = x.table_name
        LEFT JOIN pg_trigger t ON t.tgrelid = c.oid AND t.tgname = x.trigger_name AND NOT t.tgisinternal
        """;

    public static IReadOnlyDictionary<(string Schema, string FunctionName), NotificationTriggerDeployedState> Read(
        DbConnection connection, IReadOnlyList<NotificationTriggerCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<(string, string), NotificationTriggerDeployedState>();
        }

        using var command = CreateCommand(connection, candidates);
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

    public static async Task<IReadOnlyDictionary<(string Schema, string FunctionName), NotificationTriggerDeployedState>> ReadAsync(
        DbConnection connection, IReadOnlyList<NotificationTriggerCandidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<(string, string), NotificationTriggerDeployedState>();
        }

        using var command = CreateCommand(connection, candidates);
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

    private static DbCommand CreateCommand(DbConnection connection, IReadOnlyList<NotificationTriggerCandidate> candidates)
    {
        var command = connection.CreateCommand();
        command.CommandText = Sql;
        AddArrayParameter(command, "schemas", candidates.Select(c => c.Schema));
        AddArrayParameter(command, "tableNames", candidates.Select(c => c.TableName));
        AddArrayParameter(command, "functionNames", candidates.Select(c => c.FunctionName));
        AddArrayParameter(command, "triggerNames", candidates.Select(c => c.TriggerName));
        return command;
    }

    private static void AddArrayParameter(DbCommand command, string name, IEnumerable<string> values)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = values.ToArray();
        command.Parameters.Add(parameter);
    }

    private static Dictionary<(string, string), NotificationTriggerDeployedState> ReadResults(DbDataReader reader)
    {
        var results = new Dictionary<(string, string), NotificationTriggerDeployedState>();
        while (reader.Read())
        {
            AddRow(reader, results);
        }

        return results;
    }

    private static async Task<Dictionary<(string, string), NotificationTriggerDeployedState>> ReadResultsAsync(
        DbDataReader reader, CancellationToken cancellationToken)
    {
        var results = new Dictionary<(string, string), NotificationTriggerDeployedState>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AddRow(reader, results);
        }

        return results;
    }

    private static void AddRow(DbDataReader reader, Dictionary<(string, string), NotificationTriggerDeployedState> results)
    {
        var schema = reader.GetString(0);
        var functionName = reader.GetString(1);
        var fingerprintHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        var triggerExists = reader.GetBoolean(3);
        results[(schema, functionName)] = new NotificationTriggerDeployedState(fingerprintHash, triggerExists);
    }
}
