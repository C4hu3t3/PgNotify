using System.Text;
using Microsoft.EntityFrameworkCore.Storage;
using PgNotify.Model;
using PgNotify.Payloads;

namespace PgNotify.Migrations.Internal;

/// <summary>
/// Builds the deterministic, idempotent DDL for a notification trigger function and trigger:
/// <c>CREATE OR REPLACE FUNCTION</c> (so re-running is always safe and handles the "alter" case,
/// since PostgreSQL has no <c>ALTER TRIGGER ... AS</c>), <c>DROP TRIGGER IF EXISTS</c> before
/// <c>CREATE TRIGGER</c> (PostgreSQL cannot replace a trigger in place), and
/// <c>DROP FUNCTION IF EXISTS ... CASCADE</c> for cleanup.
/// </summary>
internal sealed class NotificationTriggerSqlBuilder(ISqlGenerationHelper sqlGenerationHelper)
{
    private const string DollarQuoteTag = "$notify_trigger$";

    /// <summary>
    /// The largest payload <c>pg_notify</c> accepts, in bytes. PostgreSQL's
    /// <c>NOTIFY_PAYLOAD_MAX_LENGTH</c> is 8000 including the terminating NUL; 8000 bytes of
    /// content raises <c>22023</c> "payload string too long" and, since that happens inside the
    /// trigger, aborts the write.
    /// </summary>
    private const int MaxNotifyPayloadBytes = 7999;

    /// <summary>
    /// The <c>CREATE OR REPLACE FUNCTION</c> + <c>DROP TRIGGER IF EXISTS</c> + <c>CREATE TRIGGER</c>
    /// statements needed to (re)install the trigger for <paramref name="config"/>. Empty if no
    /// operations are configured.
    /// </summary>
    /// <param name="config">The resolved notification configuration to generate SQL for.</param>
    /// <param name="allTableColumns">
    /// Every mapped column on the table, used as the "watched" set for <c>OnUpdate</c> when no
    /// explicit property selector was given (an unfiltered update trigger watches every column).
    /// </param>
    /// <param name="columnStoreTypes">
    /// Every mapped column's PostgreSQL store type (e.g. <c>json</c>, <c>character varying(100)</c>),
    /// keyed by column name, purely for lookup - <paramref name="allTableColumns"/> remains the
    /// source of column order. Used to cast columns that have no <c>=</c>/<c>IS DISTINCT FROM</c>
    /// operator (<c>json</c>, <c>xml</c>) before comparing them; a column missing from this map is
    /// treated as needing no cast.
    /// </param>
    public IReadOnlyList<string> BuildUpsertStatements(
        NotificationEntityConfiguration config,
        IReadOnlyList<string> allTableColumns,
        IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        return config.Operations == NotificationOperations.None
            ? []
            : [
            BuildCreateOrReplaceFunctionSql(config, allTableColumns, columnStoreTypes),
            BuildDropTriggerSql(config.Schema, config.TableName, config.NamePrefix),
            BuildCreateTriggerSql(config),
        ];
    }

    /// <summary>
    /// The <c>DROP TRIGGER IF EXISTS</c> + <c>DROP FUNCTION IF EXISTS ... CASCADE</c> statements
    /// to fully remove a notification trigger. Takes <paramref name="namePrefix"/> explicitly
    /// (rather than resolving it from a <see cref="NotificationEntityConfiguration"/>) because
    /// removal — notifications turned off, or the table dropped entirely — happens precisely when
    /// the current configuration can no longer answer that question; callers must supply whatever
    /// prefix was actually in effect before (see <c>NotificationFingerprint.NamePrefixAnnotationName</c>).
    /// </summary>
    public IReadOnlyList<string> BuildRemovalStatements(string? schema, string tableName, string namePrefix) =>
        [BuildDropTriggerSql(schema, tableName, namePrefix), BuildDropFunctionSql(schema, tableName, namePrefix)];

    private string BuildCreateOrReplaceFunctionSql(
        NotificationEntityConfiguration config,
        IReadOnlyList<string> allTableColumns,
        IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        var (functionName, _) = GetNames(config.Schema, config.TableName, config.NamePrefix);
        var effectiveWatchedColumns = config.WatchedUpdateColumns.Count > 0 ? config.WatchedUpdateColumns : allTableColumns;
        var fields = config.BuildPayloadFields();
        var guardsOverflow = GuardsOverflow(config);

        var body = new StringBuilder();
        var first = true;
        foreach (var operation in config.Operations.Expand())
        {
            var recordAlias = operation == NotificationOperation.Delete ? "OLD" : "NEW";
            var channel = config.GetChannelName(operation);
            var payloadExpression = BuildPayloadExpression(fields, recordAlias, operation, config, effectiveWatchedColumns, columnStoreTypes);

            var notifyLines = guardsOverflow
                ? BuildGuardedNotifyLines(config, channel, payloadExpression, recordAlias, operation, effectiveWatchedColumns, columnStoreTypes)
                : [$"PERFORM pg_notify('{Escape(channel)}', ({payloadExpression})::text);"];

            body.Append("    ").Append(first ? "IF" : "ELSIF").Append(" TG_OP = '").Append(operation.ToSqlKeyword()).AppendLine("' THEN");
            first = false;

            if (operation == NotificationOperation.Update)
            {
                var guard = BuildUpdateGuardExpression(effectiveWatchedColumns, columnStoreTypes);
                body.Append("        IF ").Append(guard).AppendLine(" THEN");
                AppendLines(body, notifyLines, "            ");
                body.AppendLine("        END IF;");
            }
            else
            {
                AppendLines(body, notifyLines, "        ");
            }
        }

        body.AppendLine("    END IF;");

        var delimitedFunctionName = sqlGenerationHelper.DelimitIdentifier(functionName, config.Schema);
        var declareSection = guardsOverflow ? "DECLARE\n    payload text;\n" : "";

        return $"""
            CREATE OR REPLACE FUNCTION {delimitedFunctionName}() RETURNS trigger
                LANGUAGE plpgsql
            AS {DollarQuoteTag}
            {declareSection}BEGIN
            {body}    RETURN NULL;
            END;
            {DollarQuoteTag}
            """;
    }

    /// <summary>
    /// Whether the function should check the payload's size and fall back to a reduced one.
    /// </summary>
    /// <remarks>
    /// Skipped for the minimal payload, which is already the shape the fallback would produce:
    /// there is nothing smaller left to send, so the check could only ever cost a comparison per
    /// row. Overflowing a minimal payload means a primary key of nearly 8 kB, which no fallback
    /// can rescue either.
    /// </remarks>
    private static bool GuardsOverflow(NotificationEntityConfiguration config) =>
        config.PayloadOverflow == NotificationPayloadOverflow.Truncate
        && config.PayloadBuilder is not MinimalNotificationPayloadBuilder;

    /// <summary>
    /// Emits the payload into a variable, swaps in a reduced payload if it would exceed
    /// <see cref="MaxNotifyPayloadBytes"/>, then notifies.
    /// </summary>
    /// <remarks>
    /// A size check rather than a PL/pgSQL <c>EXCEPTION</c> block, even though <c>pg_notify</c>'s
    /// <c>22023</c> is catchable: <c>BEGIN … EXCEPTION</c> opens a subtransaction on every
    /// invocation, which every write would pay for a case that almost never happens, and it would
    /// also swallow unrelated errors. The comparison is <c>octet_length</c>, not <c>length</c> —
    /// the server's limit is on bytes, so 4000 two-byte characters already overflow it.
    /// </remarks>
    private IReadOnlyList<string> BuildGuardedNotifyLines(
        NotificationEntityConfiguration config,
        string channel,
        string payloadExpression,
        string recordAlias,
        NotificationOperation operation,
        IReadOnlyList<string> effectiveWatchedColumns,
        IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        var reducedFields = MinimalNotificationPayloadBuilder.Instance.BuildFields(
            new NotificationPayloadBuilderContext(
                config.EntityDisplayName, config.Schema, config.TableName, config.KeyColumns, config.WatchedUpdateColumns,
                PayloadColumns: []));

        var reducedExpression = BuildPayloadExpression(
            reducedFields, recordAlias, operation, config, effectiveWatchedColumns, columnStoreTypes, markTruncated: true);

        return
        [
            $"payload := ({payloadExpression})::text;",
            $"IF octet_length(payload) > {MaxNotifyPayloadBytes} THEN",
            $"    payload := ({reducedExpression})::text;",
            "END IF;",
            $"PERFORM pg_notify('{Escape(channel)}', payload);",
        ];
    }

    private static void AppendLines(StringBuilder body, IReadOnlyList<string> lines, string indent)
    {
        foreach (var line in lines)
        {
            body.Append(indent).AppendLine(line);
        }
    }

    private string BuildDropTriggerSql(string? schema, string tableName, string namePrefix)
    {
        var (_, triggerName) = GetNames(schema, tableName, namePrefix);
        var delimitedTrigger = sqlGenerationHelper.DelimitIdentifier(triggerName);
        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(tableName, schema);
        return $"DROP TRIGGER IF EXISTS {delimitedTrigger} ON {delimitedTable}";
    }

    private string BuildCreateTriggerSql(NotificationEntityConfiguration config)
    {
        var (functionName, triggerName) = GetNames(config.Schema, config.TableName, config.NamePrefix);
        var delimitedTrigger = sqlGenerationHelper.DelimitIdentifier(triggerName);
        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(config.TableName, config.Schema);
        var delimitedFunction = sqlGenerationHelper.DelimitIdentifier(functionName, config.Schema);
        var operations = string.Join(" OR ", config.Operations.Expand().Select(o => o.ToSqlKeyword()));

        return $"""
            CREATE TRIGGER {delimitedTrigger}
                AFTER {operations} ON {delimitedTable}
                FOR EACH ROW
                EXECUTE FUNCTION {delimitedFunction}()
            """;
    }

    private string BuildDropFunctionSql(string? schema, string tableName, string namePrefix)
    {
        var (functionName, _) = GetNames(schema, tableName, namePrefix);
        var delimitedFunction = sqlGenerationHelper.DelimitIdentifier(functionName, schema);
        return $"DROP FUNCTION IF EXISTS {delimitedFunction}() CASCADE";
    }

    private string BuildPayloadExpression(
        IReadOnlyList<NotificationPayloadField> fields,
        string recordAlias,
        NotificationOperation operation,
        NotificationEntityConfiguration config,
        IReadOnlyList<string> effectiveWatchedColumns,
        IReadOnlyDictionary<string, string> columnStoreTypes,
        bool markTruncated = false)
    {
        var args = new List<string>((fields.Count + 1) * 2);
        foreach (var field in fields)
        {
            args.Add($"'{Escape(field.JsonKey)}'");
            args.Add(field.Kind switch
            {
                NotificationPayloadFieldKind.Constant => $"'{Escape(field.ConstantValue ?? string.Empty)}'::text",
                NotificationPayloadFieldKind.Operation =>
                    "(CASE TG_OP WHEN 'INSERT' THEN 'created' WHEN 'UPDATE' THEN 'updated' ELSE 'deleted' END)",
                NotificationPayloadFieldKind.Schema => $"'{Escape(config.Schema ?? string.Empty)}'::text",
                NotificationPayloadFieldKind.Table => $"'{Escape(config.TableName)}'::text",
                NotificationPayloadFieldKind.Column => $"{recordAlias}.{sqlGenerationHelper.DelimitIdentifier(field.ColumnName!)}",
                NotificationPayloadFieldKind.Keys => BuildKeysObjectExpression(config.KeyColumns, recordAlias),
                NotificationPayloadFieldKind.Changed => operation == NotificationOperation.Update
                    ? BuildChangedArrayExpression(effectiveWatchedColumns, columnStoreTypes)
                    : "ARRAY[]::text[]",
                NotificationPayloadFieldKind.Timestamp => "clock_timestamp()",
                _ => "NULL",
            });
        }

        if (markTruncated)
        {
            // Lets a consumer tell a reduced payload from a complete one without comparing shapes,
            // so it knows to re-read the row rather than trust what is missing.
            args.Add("'truncated'");
            args.Add("true");
        }

        return $"json_build_object({string.Join(", ", args)})";
    }

    private string BuildKeysObjectExpression(IReadOnlyList<string> keyColumns, string recordAlias)
    {
        var args = keyColumns.SelectMany(col =>
            new[] { $"'{Escape(col)}'", $"{recordAlias}.{sqlGenerationHelper.DelimitIdentifier(col)}" });
        return $"json_build_object({string.Join(", ", args)})";
    }

    private string BuildChangedArrayExpression(IReadOnlyList<string> watchedColumns, IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        if (watchedColumns.Count == 0)
        {
            return "ARRAY[]::text[]";
        }

        var rows = watchedColumns.Select(col =>
            $"('{Escape(col)}', {BuildDistinctFromExpression(col, columnStoreTypes)})");
        return $"ARRAY(SELECT t.col FROM (VALUES {string.Join(", ", rows)}) AS t(col, changed) WHERE t.changed)";
    }

    private string BuildUpdateGuardExpression(IReadOnlyList<string> watchedColumns, IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        if (watchedColumns.Count == 0)
        {
            return "TRUE";
        }

        var checks = watchedColumns.Select(col => $"({BuildDistinctFromExpression(col, columnStoreTypes)})");
        return string.Join(" OR ", checks);
    }

    /// <summary>
    /// A <c>NEW.col IS DISTINCT FROM OLD.col</c> comparison, casting both sides first for store
    /// types that have no <c>=</c>/<c>IS DISTINCT FROM</c> operator at all: <c>json</c> (cast to
    /// <c>jsonb</c>, which also has the benefit of comparing by parsed value rather than by exact
    /// text - two JSON documents differing only in key order compare equal) and <c>xml</c> (cast to
    /// <c>text</c>). Every other store type is compared as-is. Without this, <c>CREATE FUNCTION</c>
    /// still succeeds (PL/pgSQL only type-checks an expression the first time it runs), but the
    /// first <c>UPDATE</c> touching the row aborts the whole transaction with
    /// <c>operator does not exist: json = json</c> (or <c>xml = xml</c>) - confirmed against a real
    /// PostgreSQL 16 instance.
    /// </summary>
    private string BuildDistinctFromExpression(string column, IReadOnlyDictionary<string, string> columnStoreTypes)
    {
        var delimited = sqlGenerationHelper.DelimitIdentifier(column);
        var cast = columnStoreTypes.TryGetValue(column, out var storeType) ? GetComparisonCast(storeType) : "";
        return $"NEW.{delimited}{cast} IS DISTINCT FROM OLD.{delimited}{cast}";
    }

    private static string GetComparisonCast(string storeType) => storeType.ToLowerInvariant() switch
    {
        "json" => "::jsonb",
        "xml" => "::text",
        _ => "",
    };

    /// <summary>
    /// Exposed internally so <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions"/>
    /// can compute the exact same (prefixed, length-truncated) names to check what's already
    /// deployed before deciding whether to (re)run <see cref="BuildUpsertStatements"/> at all.
    /// </summary>
    internal static (string FunctionName, string TriggerName) GetNames(string? schema, string tableName, string namePrefix)
    {
        var functionName = PostgresIdentifier.EnsureWithinLength($"{namePrefix}fn_{(schema is null ? "" : schema + "_")}{tableName}_notify");
        var triggerName = PostgresIdentifier.EnsureWithinLength($"{namePrefix}trg_{tableName}_notify");
        return (functionName, triggerName);
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
