namespace PgNotify.Migrations;

/// <summary>
/// A notification trigger/function pair found deployed in the database that the current EF Core
/// model no longer accounts for — most often because <c>[NotifyChanges]</c>/
/// <c>HasDatabaseNotifications()</c> was removed from the entity, or its table/name prefix changed,
/// since the DDL was applied. Returned by
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions.FindOrphanedNotificationTriggers"/>/
/// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions.FindOrphanedNotificationTriggersAsync"/>
/// for the database-first path, which — unlike the migrations path — has no "old model" to diff
/// against and so never drops anything on its own.
/// </summary>
/// <param name="Schema">The schema the deployed trigger's table lives in ("public" unless overridden).</param>
/// <param name="TableName">The table the deployed trigger is attached to.</param>
/// <param name="FunctionName">The deployed trigger function's name.</param>
/// <param name="TriggerName">The deployed trigger's name.</param>
/// <param name="DropStatements">
/// <c>DROP TRIGGER IF EXISTS</c> then <c>DROP FUNCTION IF EXISTS ... CASCADE</c>, built from the
/// names actually found deployed — review before running; this library never executes them itself.
/// </param>
public sealed record OrphanedNotificationTrigger(
    string Schema,
    string TableName,
    string FunctionName,
    string TriggerName,
    IReadOnlyList<string> DropStatements);
