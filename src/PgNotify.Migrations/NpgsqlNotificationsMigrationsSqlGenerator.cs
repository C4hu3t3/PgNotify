using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;
using PgNotify;
using PgNotify.Migrations.Internal;
using PgNotify.Model;

namespace PgNotify.Migrations;

/// <summary>
/// Extends Npgsql's migrations SQL generator to emit trigger/trigger-function DDL for entities
/// with database notifications enabled, driven by the <see cref="NotificationFingerprint"/>
/// annotation that <see cref="NpgsqlNotificationsAnnotationProvider"/> and
/// <see cref="NpgsqlNotificationsMigrationsAnnotationProvider"/> attach to table operations.
/// </summary>
public class NpgsqlNotificationsMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlSingletonOptions)
    : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
{
    private readonly NotificationTriggerSqlBuilder _triggerSqlBuilder = new(dependencies.SqlGenerationHelper);
    private readonly NotificationReplicationSqlBuilder _replicationSqlBuilder = new(dependencies.SqlGenerationHelper);

    /// <inheritdoc />
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        base.Generate(operation, model, builder, terminate: true);

        if (operation.FindAnnotation(NotificationReplicationFingerprint.AnnotationName) is not null)
        {
            var replicationConfig = ResolveConfiguration(model, operation.Schema, operation.Name);
            if (replicationConfig is not null)
            {
                EmitStatements(builder, _replicationSqlBuilder.BuildUpsertStatements(replicationConfig));
            }

            return;
        }

        if (operation.FindAnnotation(NotificationFingerprint.AnnotationName) is null)
        {
            return;
        }

        var config = ResolveConfiguration(model, operation.Schema, operation.Name);
        if (config is not null)
        {
            var (allColumns, columnStoreTypes) = GetColumnInfo(model, operation.Schema, operation.Name);
            EmitStatements(builder, _triggerSqlBuilder.BuildUpsertStatements(config, allColumns, columnStoreTypes));
        }
    }

    /// <inheritdoc />
    protected override void Generate(AlterTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        base.Generate(operation, model, builder);

        // The two fingerprints are mutually exclusive per table (one delivery mode each), but both
        // channels are always checked independently: that is what makes a mode switch (Notify <->
        // LogicalReplication in one migration) correct by construction rather than a special case —
        // one channel sees its fingerprint disappear (removal), the other sees its fingerprint
        // appear (upsert), with no coordination between them needed.
        GenerateTriggerAlterations(operation, model, builder);
        GenerateReplicationAlterations(operation, model, builder);
    }

    private void GenerateTriggerAlterations(AlterTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        var newFingerprint = (string?)operation.FindAnnotation(NotificationFingerprint.AnnotationName)?.Value;
        var oldFingerprint = (string?)operation.OldTable.FindAnnotation(NotificationFingerprint.AnnotationName)?.Value;

        if (newFingerprint == oldFingerprint)
        {
            // No notification-relevant change; avoid regenerating trigger SQL for unrelated alterations.
            return;
        }

        if (newFingerprint is null)
        {
            var namePrefix = (string?)operation.OldTable.FindAnnotation(NotificationFingerprint.NamePrefixAnnotationName)?.Value ?? "";
            EmitStatements(builder, _triggerSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, namePrefix));
            return;
        }

        var config = ResolveConfiguration(model, operation.Schema, operation.Name);
        if (config is not null)
        {
            // A changed NamePrefix moves the fingerprint (it is part of the hashed configuration),
            // so this branch runs - but resolving the *current* config only ever produces the
            // *new*-prefixed CREATE/DROP-then-CREATE pair. Without this, the old-prefixed trigger
            // and function are never dropped: both stay live, both fire, and every write is
            // notified twice under whatever naming strategy is in effect. Drop them first, using
            // the table's current name - it still exists at this point, and OldTable only ever
            // carries the prefix that was in effect before this operation, never a table rename
            // (that is Generate(RenameTableOperation, ...)'s job, not this one's).
            var oldPrefix = (string?)operation.OldTable.FindAnnotation(NotificationFingerprint.NamePrefixAnnotationName)?.Value ?? "";
            if (oldPrefix != config.NamePrefix)
            {
                EmitStatements(builder, _triggerSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, oldPrefix));
            }

            var (allColumns, columnStoreTypes) = GetColumnInfo(model, operation.Schema, operation.Name);
            EmitStatements(builder, _triggerSqlBuilder.BuildUpsertStatements(config, allColumns, columnStoreTypes));
        }
    }

    private void GenerateReplicationAlterations(AlterTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        var newFingerprint = (string?)operation.FindAnnotation(NotificationReplicationFingerprint.AnnotationName)?.Value;
        var oldFingerprint = (string?)operation.OldTable.FindAnnotation(NotificationReplicationFingerprint.AnnotationName)?.Value;

        if (newFingerprint == oldFingerprint)
        {
            return;
        }

        if (newFingerprint is null)
        {
            var namePrefix = (string?)operation.OldTable.FindAnnotation(NotificationReplicationFingerprint.NamePrefixAnnotationName)?.Value ?? "";
            EmitStatements(builder, _replicationSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, namePrefix));
            return;
        }

        var config = ResolveConfiguration(model, operation.Schema, operation.Name);
        if (config is not null)
        {
            // Same reasoning as the trigger side's NamePrefix handling: the publication name
            // ({prefix}pgnotify_pub) embeds the prefix, so a changed prefix alone would otherwise
            // leave this table a member of the old-prefixed publication forever, replicating it
            // through both.
            var oldPrefix = (string?)operation.OldTable.FindAnnotation(NotificationReplicationFingerprint.NamePrefixAnnotationName)?.Value ?? "";
            if (oldPrefix != config.NamePrefix)
            {
                EmitStatements(builder, _replicationSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, oldPrefix));
            }

            EmitStatements(builder, _replicationSqlBuilder.BuildUpsertStatements(config));
        }
    }

    /// <inheritdoc />
    protected override void Generate(RenameTableOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        // Trigger/function names embed the table name (NotificationTriggerSqlBuilder.GetNames), so a
        // rename alone moves them even though nothing about the notification configuration itself
        // changed. The differ also emits an AlterTableOperation right after this one, because the
        // fingerprint (which hashes the generated SQL, and that SQL contains the table name) now
        // differs - but that operation only ever resolves the *current* config and only ever knows
        // the *new* table name, the same gap 1.1 fixed for NamePrefix changes. RenameTableOperation
        // carries no OldTable-style annotation to recover the old name from, so without this, the
        // old-named trigger and function are orphaned and keep firing forever alongside the pair the
        // AlterTableOperation creates under the new name. This must run before the rename: DROP
        // TRIGGER needs the table to still be reachable under its old name.
        //
        // Scoped to a rename with no simultaneous notification-configuration change - a rename that
        // also changes NamePrefix (or turns notifications off) in the same migration is not covered.
        //
        // LogicalReplication needs none of this: neither the publication name nor the slot name
        // embed the table name (NotificationReplicationSqlBuilder.GetPublicationName/GetSlotName are
        // prefix/consumer-group scoped only), and PostgreSQL tracks publication membership by the
        // table's OID, which a plain rename does not change - membership survives automatically.
        var config = ResolveConfiguration(model, operation.NewSchema ?? operation.Schema, operation.NewName ?? operation.Name);
        if (config is { DeliveryMode: NotificationDeliveryMode.Notify })
        {
            EmitStatements(builder, _triggerSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, config.NamePrefix));
        }

        base.Generate(operation, model, builder);
    }

    /// <inheritdoc />
    protected override void Generate(DropTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (operation.FindAnnotation(NotificationReplicationFingerprint.AnnotationName) is not null)
        {
            var namePrefix = (string?)operation.FindAnnotation(NotificationReplicationFingerprint.NamePrefixAnnotationName)?.Value ?? "";

            // ALTER PUBLICATION ... DROP TABLE needs the table to still exist (it resolves the
            // relation via to_regclass), so this must run before the base DROP TABLE.
            EmitStatements(builder, _replicationSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, namePrefix));
        }
        else if (operation.FindAnnotation(NotificationFingerprint.AnnotationName) is not null)
        {
            var namePrefix = (string?)operation.FindAnnotation(NotificationFingerprint.NamePrefixAnnotationName)?.Value ?? "";

            // DROP TRIGGER needs the table to still exist, so this must run before the base DROP TABLE.
            EmitStatements(builder, _triggerSqlBuilder.BuildRemovalStatements(operation.Schema, operation.Name, namePrefix));
        }

        base.Generate(operation, model, builder, terminate);
    }

    private void EmitStatements(MigrationCommandListBuilder builder, IReadOnlyList<string> statements)
    {
        foreach (var statement in statements)
        {
            builder.Append(statement).AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);
            EndStatement(builder, suppressTransaction: false);
        }
    }

    private static NotificationEntityConfiguration? ResolveConfiguration(IModel? model, string? schema, string tableName)
    {
        var entityType = FindEntityType(model, schema, tableName);
        return entityType?.GetNotificationConfiguration();
    }

    private static (string[] Names, IReadOnlyDictionary<string, string> StoreTypes) GetColumnInfo(IModel? model, string? schema, string tableName)
    {
        var columns = model?.GetRelationalModel().FindTable(tableName, schema)?.Columns.ToArray() ?? [];
        return (
            [.. columns.Select(c => c.Name)],
            columns.ToDictionary(c => c.Name, c => c.StoreType));
    }

    private static IEntityType? FindEntityType(IModel? model, string? schema, string tableName)
    {
        var table = model?.GetRelationalModel().FindTable(tableName, schema);
        return table?.EntityTypeMappings.FirstOrDefault()?.TypeBase as IEntityType;
    }
}
