using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using PgNotify.Metadata;

namespace PgNotify.Conventions;

/// <summary>
/// Validates notification configuration once the model is otherwise finalized, so mistakes fail
/// fast at <c>OnModelCreating</c> time rather than surfacing as confusing SQL generation or
/// runtime errors later. Complements (does not replace) the compile-time
/// <c>PgNotify.Analyzers</c> diagnostics, which catch some of the same mistakes earlier,
/// at edit time, for attribute/fluent API misuse the analyzer can see statically.
/// </summary>
public sealed class NotificationValidationConvention : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entityTypesByTable = modelBuilder.Metadata.GetEntityTypes()
            .Where(candidate => candidate.GetTableName() is not null)
            .ToLookup(candidate => (candidate.GetSchema(), candidate.GetTableName()!));

        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            if (!entityType.IsNotificationsEnabled())
            {
                continue;
            }

            if (entityType.FindPrimaryKey() is null)
            {
                throw new InvalidOperationException(
                    $"Entity '{entityType.DisplayName()}' has database notifications enabled but no primary key. "
                    + "Notifications require a primary key to identify the affected row.");
            }

            RejectUnmappedToTable(entityType);
            RejectSharedTable(entityType, entityTypesByTable);

            foreach (var propertyName in GetWatchedPropertyNames(entityType))
            {
                var property = entityType.FindProperty(propertyName);
                if (property is null)
                {
                    var isNavigation = entityType.FindNavigation(propertyName) is not null
                        || entityType.FindSkipNavigation(propertyName) is not null;

                    throw new InvalidOperationException(
                        isNavigation
                            ? $"Entity '{entityType.DisplayName()}' watches '{propertyName}' for updates, but it is a navigation "
                              + "property, not a scalar column. Only mapped scalar properties can be watched."
                            : $"Entity '{entityType.DisplayName()}' watches '{propertyName}' for updates, but no such property "
                              + "was found on the entity.");
                }
            }

            foreach (var propertyName in GetPayloadPropertyNames(entityType))
            {
                if (entityType.FindProperty(propertyName) is not null)
                {
                    continue;
                }

                var isNavigation = entityType.FindNavigation(propertyName) is not null
                    || entityType.FindSkipNavigation(propertyName) is not null;

                throw new InvalidOperationException(
                    isNavigation
                        ? $"Entity '{entityType.DisplayName()}' projects '{propertyName}' into its notification payload, but it "
                          + "is a navigation property, not a scalar column. The trigger reads projected values straight off "
                          + "NEW/OLD, so only mapped scalar properties can be projected."
                        : $"Entity '{entityType.DisplayName()}' projects '{propertyName}' into its notification payload, but no "
                          + "such property was found on the entity.");
            }

            RejectFilteredUpdateWithoutReplicaIdentity(entityType);

            // Resolving the configuration eagerly surfaces custom channel-strategy/payload-builder
            // construction failures (missing parameterless constructor, unloadable type) at model
            // build time instead of at first migration or runtime use.
            _ = entityType.GetNotificationConfiguration();
        }
    }

    /// <summary>
    /// Refuses notifications configured on an entity that isn't mapped to a table at all — the
    /// resolved configuration otherwise comes back <see langword="null"/> from
    /// <see cref="EntityTypeNotificationExtensions.GetNotificationConfiguration"/> (silently: no
    /// trigger, no error), because there is no table name to attach one to. Distinguishes a
    /// view-only mapping (<c>ToView(...)</c> without a matching <c>ToTable(...)</c>) from every
    /// other reason an entity might lack a table, since the former is the mistake someone is most
    /// likely to actually make.
    /// </summary>
    private static void RejectUnmappedToTable(IConventionEntityType entityType)
    {
        if (entityType.GetTableName() is not null)
        {
            return;
        }

        if (entityType.GetViewName() is { } viewName)
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' has database notifications enabled, but it is mapped only to view "
                + $"'{viewName}', not to a table. A notification trigger is DDL on a table; map "
                + $"'{entityType.DisplayName()}' to its underlying table with ToTable(...) as well (a common pattern when "
                + "the view is used for querying and the table for writes), or remove the notification configuration.");
        }

        throw new InvalidOperationException(
            $"Entity '{entityType.DisplayName()}' has database notifications enabled, but it is not mapped to a table. "
            + "Notifications require a table to attach a trigger to.");
    }

    /// <summary>
    /// Refuses notifications configured on a type whose table is also mapped by another, unrelated
    /// entity type — table splitting — and, as before, on a type that shares its table with a base
    /// type (table-per-hierarchy). Both are the same underlying problem: a trigger belongs to a
    /// table, and every provider entry point resolves a table's configuration through its first
    /// entity mapping (<c>table.EntityTypeMappings.First()</c>), silently ignoring configuration on
    /// every other type mapped into it — measured for table splitting to be strictly worse than the
    /// table-per-hierarchy case it was first caught for: which of two split entities
    /// <c>EntityTypeMappings.First()</c> returns is an EF Core implementation detail, not
    /// something either entity's own configuration controls, so notifications configured on the
    /// "wrong" one produce no trigger and no error, even when only that one entity is configured.
    /// </summary>
    /// <remarks>
    /// Table-per-type and table-per-concrete-type are untouched, and verified to generate their own
    /// trigger, because there the derived type owns a table of its own. Configuring the TPH root
    /// instead works, with a consequence worth knowing: the trigger sees every row in the table, so
    /// a derived row notifies under the root's entity name. Table splitting has no equivalent
    /// redirect — the two entities are peers, not root and derived — so it is always refused; see
    /// the "support two independent triggers" follow-up for a design that would allow it.
    /// </remarks>
    private static void RejectSharedTable(
        IConventionEntityType entityType,
        ILookup<(string? Schema, string Table), IConventionEntityType> entityTypesByTable)
    {
        var tableName = entityType.GetTableName();
        if (tableName is null)
        {
            return;
        }

        var schema = entityType.GetSchema();
        var root = entityType.GetRootType();

        foreach (var sibling in entityTypesByTable[(schema, tableName)])
        {
            if (sibling == entityType)
            {
                continue;
            }

            if (sibling.GetRootType() != root)
            {
                throw new InvalidOperationException(
                    $"Entity '{entityType.DisplayName()}' has database notifications enabled, but it shares table '{tableName}' "
                    + $"with unrelated entity '{sibling.DisplayName()}' (table splitting). A notification trigger belongs to the "
                    + "table, not to one type mapped into it, so only one entity sharing a table can have notifications enabled "
                    + $"— and which one silently loses depends on EF Core's internal mapping order, not on either entity's own "
                    + $"configuration. Map '{entityType.DisplayName()}' to its own table if it needs a notification of its own.");
            }
        }

        if (root == entityType || root.GetTableName() != tableName || root.GetSchema() != schema)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Entity '{entityType.DisplayName()}' has database notifications enabled, but it shares table '{tableName}' with "
            + $"its base type '{root.DisplayName()}' (table-per-hierarchy). A notification trigger belongs to the table, not to "
            + $"one type mapped into it, so configure notifications on '{root.DisplayName()}' instead — its trigger fires for "
            + $"every row of the table, including '{entityType.DisplayName()}' rows, which are then reported under the entity "
            + $"name '{root.ShortName()}'. Map '{entityType.DisplayName()}' to its own table if it needs a notification of its own.");
    }

    /// <summary>
    /// Under <see cref="NotificationDeliveryMode.LogicalReplication"/>, a filtered
    /// <c>OnUpdate(x =&gt; new { ... })</c> can only be enforced client-side by comparing the
    /// replication stream's old and new row values — and the old values only exist on the wire
    /// when the table's <c>REPLICA IDENTITY</c> is <c>FULL</c>. Under
    /// <see cref="NotificationDeliveryMode.Notify"/> this has no bearing at all: the trigger's own
    /// <c>IS DISTINCT FROM</c> guard needs no such setting. Rejected at model-build time rather
    /// than left to either silently never fire or silently fire on every update.
    /// </summary>
    private static void RejectFilteredUpdateWithoutReplicaIdentity(IConventionEntityType entityType)
    {
        var deliveryMode = Enum.TryParse<NotificationDeliveryMode>(
            (string?)entityType.FindAnnotation(NotificationAnnotationNames.DeliveryMode)?.Value,
            out var mode)
            ? mode
            : NotificationDeliveryMode.Notify;

        if (deliveryMode != NotificationDeliveryMode.LogicalReplication)
        {
            return;
        }

        var replicaIdentityFull = entityType.FindAnnotation(NotificationAnnotationNames.ReplicaIdentityFull) is { Value: true };
        if (replicaIdentityFull || GetWatchedPropertyNames(entityType).Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Entity '{entityType.DisplayName()}' uses NotificationDeliveryMode.LogicalReplication with a filtered "
            + "OnUpdate(...), but does not opt into WithReplicaIdentityFull(). Under logical replication, filtering is "
            + "enforced by comparing old and new row values from the replication stream, and old values are only present "
            + "when the table's REPLICA IDENTITY is FULL — without it, there is nothing to compare a watched property "
            + "against. Call WithReplicaIdentityFull() (accepting its WAL-volume cost), or drop the property filter to "
            + "watch every mapped column instead.");
    }

    private static string[] GetWatchedPropertyNames(IConventionEntityType entityType) =>
        SplitAnnotation(entityType, NotificationAnnotationNames.WatchedUpdateColumns);

    private static string[] GetPayloadPropertyNames(IConventionEntityType entityType) =>
        SplitAnnotation(entityType, NotificationAnnotationNames.PayloadColumns);

    private static string[] SplitAnnotation(IConventionEntityType entityType, string annotationName)
    {
        var raw = (string?)entityType.FindAnnotation(annotationName)?.Value;
        return string.IsNullOrEmpty(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }
}
