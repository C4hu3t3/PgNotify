using System.Text;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using PgNotify;
using PgNotify.Migrations.Internal;
using PgNotify.Model;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, alongside EnsureCreated()/
// MigrateAsync() and UseNpgsqlNotifications().
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Applies (or scripts) notification trigger/trigger-function DDL directly from the current EF
/// Core model, without going through <c>dotnet ef migrations</c> — for database-first projects
/// where schema (including this library's triggers) is owned by another tool (Flyway, DbUp, hand
/// -written SQL, or an already-existing database) rather than by EF Core's migrations history.
/// </summary>
/// <remarks>
/// This assumes the underlying tables already exist — it only ever emits
/// <c>CREATE OR REPLACE FUNCTION</c> / <c>DROP TRIGGER IF EXISTS</c> / <c>CREATE TRIGGER</c>, the
/// same idempotent statements <c>PgNotify.Migrations</c>' <c>IMigrationsSqlGenerator</c>
/// emits for a migration (see <c>docs/migrations.md</c>). Unlike the migrations path, there is no
/// diff against a previous state tracked by EF Core itself; instead, <see cref="EnsureNotificationTriggers"/>/
/// <see cref="EnsureNotificationTriggersAsync"/> read each function's current
/// <c>COMMENT ON FUNCTION</c> (a SHA-256 hash of the same fingerprint <c>Notifications:Fingerprint</c>
/// is computed from for the migrations path — see <c>NotificationFingerprint.ComputeHash</c>) and
/// skip an entity entirely when the deployed hash already matches and its trigger still exists —
/// one read query, then only the DDL actually needed. This does not
/// drop triggers for entities that used to be configured but no longer are (do that manually, the
/// same as you would for any other schema element a database-first workflow retired).
/// </remarks>
public static class DatabaseFacadeNotificationsExtensions
{
    /// <summary>Applies the notification trigger/function DDL for every configured entity that isn't already up to date. Requires <c>UseNpgsqlNotifications()</c> to have been chained onto <c>UseNpgsql(...)</c>.</summary>
    public static void EnsureNotificationTriggers(this DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (logger, work) = PrepareWork(database);
        var deployed = NotificationTriggerStateReader.Read(database.GetDbConnection(), BuildCandidates(work));

        foreach (var item in work)
        {
            if (IsUpToDate(deployed, item))
            {
                LogUpToDate(logger, item.Config);
                continue;
            }

            foreach (var statement in item.Statements)
            {
                database.ExecuteSqlRaw(statement);
            }

            LogApplied(logger, item.Config);
        }
    }

    /// <summary>Applies the notification trigger/function DDL for every configured entity that isn't already up to date. Requires <c>UseNpgsqlNotifications()</c> to have been chained onto <c>UseNpgsql(...)</c>.</summary>
    public static async Task EnsureNotificationTriggersAsync(this DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (logger, work) = PrepareWork(database);
        var deployed = await NotificationTriggerStateReader.ReadAsync(database.GetDbConnection(), BuildCandidates(work), cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in work)
        {
            if (IsUpToDate(deployed, item))
            {
                LogUpToDate(logger, item.Config);
                continue;
            }

            foreach (var statement in item.Statements)
            {
                await database.ExecuteSqlRawAsync(statement, cancellationToken).ConfigureAwait(false);
            }

            LogApplied(logger, item.Config);
        }
    }

    /// <summary>
    /// Renders the same DDL <see cref="EnsureNotificationTriggersAsync"/> would execute (for every
    /// configured entity, unconditionally — this doesn't touch the database to check what's
    /// already deployed) as a single SQL script, for database-first projects that check trigger
    /// DDL into their own migration tool (Flyway, DbUp, ...) instead of having this library apply
    /// it directly.
    /// </summary>
    public static string GenerateNotificationTriggersScript(this DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (_, work) = PrepareWork(database);
        var sqlGenerationHelper = database.GetService<ISqlGenerationHelper>();

        var script = new StringBuilder();
        foreach (var item in work)
        {
            script.Append("-- ").Append(item.Config.EntityDisplayName).Append(" (")
                .Append(item.Config.Schema is null ? "" : item.Config.Schema + ".").Append(item.Config.TableName).AppendLine(")");

            foreach (var statement in item.Statements)
            {
                script.Append(statement).AppendLine(sqlGenerationHelper.StatementTerminator);
            }

            script.AppendLine();
        }

        return script.ToString();
    }

    private static IReadOnlyList<NotificationTriggerCandidate> BuildCandidates(IReadOnlyList<NotificationWorkItem> work) =>
        [.. work.Select(item => new NotificationTriggerCandidate(item.Schema, item.Config.TableName, item.FunctionName, item.TriggerName))];

    private static bool IsUpToDate(
        IReadOnlyDictionary<(string Schema, string FunctionName), NotificationTriggerDeployedState> deployed,
        NotificationWorkItem item) =>
        deployed.TryGetValue((item.Schema, item.FunctionName), out var state)
        && state.TriggerExists
        && state.FingerprintHash == item.FingerprintHash;

    private static (ILogger? Logger, IReadOnlyList<NotificationWorkItem> Work) PrepareWork(DatabaseFacade database)
    {
        var context = database.GetService<ICurrentDbContext>().Context;
        var sqlGenerationHelper = database.GetService<ISqlGenerationHelper>();
        var logger = database.GetService<ILoggerFactory>()
            .CreateLogger("PgNotify.Migrations");

        var triggerSqlBuilder = new NotificationTriggerSqlBuilder(sqlGenerationHelper);
        var relationalModel = context.Model.GetRelationalModel();

        var work = new List<NotificationWorkItem>();
        var processedTables = new HashSet<(string? Schema, string Table)>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var config = entityType.GetNotificationConfiguration();
            if (config is null || config.Operations == NotificationOperations.None || !processedTables.Add((config.Schema, config.TableName)))
            {
                continue;
            }

            var tableColumns = relationalModel.FindTable(config.TableName, config.Schema)?.Columns.ToArray() ?? [];
            var allColumns = tableColumns.Select(c => c.Name).ToArray();
            var columnStoreTypes = tableColumns.ToDictionary(c => c.Name, c => c.StoreType);

            var upsertStatements = triggerSqlBuilder.BuildUpsertStatements(config, allColumns, columnStoreTypes);
            var (functionName, triggerName) = NotificationTriggerSqlBuilder.GetNames(config.Schema, config.TableName, config.NamePrefix);
            var fingerprint = NotificationFingerprint.ComputeHash(config, upsertStatements);
            var commentStatement = BuildCommentStatement(sqlGenerationHelper, config.Schema, functionName, fingerprint);

            work.Add(new NotificationWorkItem(
                config,
                Schema: config.Schema ?? "public",
                FunctionName: functionName,
                TriggerName: triggerName,
                FingerprintHash: fingerprint,
                Statements: [.. upsertStatements, commentStatement]));
        }

        return (logger, work);
    }

    private static string BuildCommentStatement(ISqlGenerationHelper sqlGenerationHelper, string? schema, string functionName, string fingerprintHash)
    {
        var delimitedFunction = sqlGenerationHelper.DelimitIdentifier(functionName, schema);
        return $"COMMENT ON FUNCTION {delimitedFunction}() IS '{fingerprintHash}'";
    }

    private static void LogApplied(ILogger? logger, NotificationEntityConfiguration config) =>
        logger?.LogInformation(
            "Ensured notification trigger for {Table}",
            config.Schema is null ? config.TableName : $"{config.Schema}.{config.TableName}");

    private static void LogUpToDate(ILogger? logger, NotificationEntityConfiguration config) =>
        logger?.LogDebug(
            "Notification trigger for {Table} is already up to date; skipped",
            config.Schema is null ? config.TableName : $"{config.Schema}.{config.TableName}");

    private sealed record NotificationWorkItem(
        NotificationEntityConfiguration Config,
        string Schema,
        string FunctionName,
        string TriggerName,
        string FingerprintHash,
        IReadOnlyList<string> Statements);
}
