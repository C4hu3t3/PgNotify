using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using PgNotify.Migrations.Internal;

namespace PgNotify.Migrations;

/// <summary>
/// Surfaces the notification fingerprint annotation on <see cref="DropTableOperation"/>. EF
/// Core's migrations differ uses a distinct code path (<see cref="IMigrationsAnnotationProvider"/>,
/// not <see cref="IRelationalAnnotationProvider"/>) to decide which annotations flow onto
/// removal operations, so this needs its own override even though the "create/alter" case is
/// already covered by <see cref="NpgsqlNotificationsAnnotationProvider"/>.
/// </summary>
/// <remarks>
/// Derives from EF Core's base <see cref="MigrationsAnnotationProvider"/> rather than a
/// Npgsql-specific subclass: the installed Npgsql.EntityFrameworkCore.PostgreSQL version does not
/// register a provider-specific <see cref="IMigrationsAnnotationProvider"/>, so the base
/// implementation (which returns no annotations for removal by default) is what Npgsql itself
/// relies on today.
/// </remarks>
public class NpgsqlNotificationsMigrationsAnnotationProvider(
    MigrationsAnnotationProviderDependencies dependencies,
    ISqlGenerationHelper sqlGenerationHelper)
    : MigrationsAnnotationProvider(dependencies)
{
    private readonly NotificationTriggerSqlBuilder _triggerSqlBuilder = new(sqlGenerationHelper);

    /// <inheritdoc />
    public override IEnumerable<IAnnotation> ForRemove(ITable table)
    {
        foreach (var annotation in base.ForRemove(table))
        {
            yield return annotation;
        }

        var entityType = (IEntityType)table.EntityTypeMappings.First().TypeBase;
        var config = entityType.GetNotificationConfiguration();
        if (config is null)
        {
            yield break;
        }

        // DropTableOperation only ever tests this annotation for presence, never for equality, but
        // it is computed the same way as everywhere else rather than with a marker value: which
        // annotations get compared is EF Core's business, not something to encode an assumption
        // about here.
        var statements = _triggerSqlBuilder.BuildUpsertStatements(
            config,
            [.. table.Columns.Select(column => column.Name)],
            table.Columns.ToDictionary(column => column.Name, column => column.StoreType));

        yield return new Annotation(NotificationFingerprint.AnnotationName, NotificationFingerprint.Compute(config, statements));
        yield return new Annotation(NotificationFingerprint.NamePrefixAnnotationName, config.NamePrefix);
    }
}
