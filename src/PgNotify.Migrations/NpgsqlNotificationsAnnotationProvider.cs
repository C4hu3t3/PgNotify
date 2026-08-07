using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.Internal;
using PgNotify.Migrations.Internal;

namespace PgNotify.Migrations;

/// <summary>
/// Extends Npgsql's own table annotation provider with a single notification "fingerprint"
/// annotation, present only at design time (matching the base provider's own convention for
/// design-time-only facets). This is what lets EF Core's migrations differ detect notification
/// configuration changes even when no column/constraint actually changed.
/// </summary>
public class NpgsqlNotificationsAnnotationProvider(
    RelationalAnnotationProviderDependencies dependencies,
    ISqlGenerationHelper sqlGenerationHelper)
    : NpgsqlAnnotationProvider(dependencies)
{
    // The fingerprint hashes the SQL this builder produces, so the provider needs the same
    // ISqlGenerationHelper the migrations SQL generator uses - EF Core's internal container
    // resolves it here without any extra registration.
    private readonly NotificationTriggerSqlBuilder _triggerSqlBuilder = new(sqlGenerationHelper);

    /// <inheritdoc />
    public override IEnumerable<IAnnotation> For(ITable table, bool designTime)
    {
        foreach (var annotation in base.For(table, designTime))
        {
            yield return annotation;
        }

        if (!designTime)
        {
            yield break;
        }

        var entityType = (IEntityType)table.EntityTypeMappings.First().TypeBase;
        var config = entityType.GetNotificationConfiguration();
        if (config is null)
        {
            yield break;
        }

        // table.Columns is the table's real column set, so an unfiltered OnUpdate() - which
        // watches every mapped column - produces a fingerprint that reacts to a column being
        // added or removed, not just to notification configuration changing. Store types flow
        // through the same way, so a bare column-type change (text -> json) also moves the
        // fingerprint even though nothing about the notification configuration changed.
        var allTableColumns = table.Columns.Select(column => column.Name).ToArray();
        var columnStoreTypes = table.Columns.ToDictionary(column => column.Name, column => column.StoreType);
        var statements = _triggerSqlBuilder.BuildUpsertStatements(config, allTableColumns, columnStoreTypes);

        yield return new Annotation(NotificationFingerprint.AnnotationName, NotificationFingerprint.Compute(config, statements));

        // Also surfaced here (not just from NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove):
        // AlterTableOperation.OldTable's annotations come from this provider (via ITable.GetAnnotations(),
        // populated when the RelationalModel is built), not from IMigrationsAnnotationProvider.ForRemove
        // — that path is only used when the whole table is being dropped (DropTableOperation), not when
        // notifications are merely turned off on a table that still exists.
        yield return new Annotation(NotificationFingerprint.NamePrefixAnnotationName, config.NamePrefix);
    }
}
