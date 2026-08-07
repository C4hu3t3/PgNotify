using Microsoft.EntityFrameworkCore.Metadata;
using PgNotify.Metadata;
using PgNotify.Model;
using PgNotify.Naming;
using PgNotify.Payloads;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, alongside EF Core's own
// relational metadata extension methods (GetTableName, GetSchema, ...), so it is discoverable
// without an extra using directive wherever EF Core metadata is already in scope.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Read-side access to an entity type's PostgreSQL notification configuration, resolved from the
/// annotations written by <c>HasDatabaseNotifications()</c> or <c>[NotifyChanges]</c>.
/// </summary>
public static class EntityTypeNotificationExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="entityType"/> has notifications enabled.
    /// </summary>
    public static bool IsNotificationsEnabled(this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return entityType.FindAnnotation(NotificationAnnotationNames.Enabled) is { Value: true };
    }

    /// <summary>
    /// Resolves the full notification configuration for <paramref name="entityType"/>, or
    /// <see langword="null"/> if notifications are not enabled or the entity is not mapped to a table.
    /// </summary>
    public static NotificationEntityConfiguration? GetNotificationConfiguration(this IReadOnlyEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (!entityType.IsNotificationsEnabled())
        {
            return null;
        }

        var tableName = entityType.GetTableName();
        if (tableName is null)
        {
            return null;
        }

        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        var keyColumns = entityType.FindPrimaryKey()?.Properties
            .Select(p => p.GetColumnName(storeObject) ?? p.Name)
            .ToArray() ?? [];

        var operations = NotificationConfigurationWriter.ParseOperations(
            (string?)entityType.FindAnnotation(NotificationAnnotationNames.Operations)?.Value);

        var watchedPropertyNames = SplitCsv((string?)entityType.FindAnnotation(NotificationAnnotationNames.WatchedUpdateColumns)?.Value);
        var watchedColumns = watchedPropertyNames
            .Select(propertyName => entityType.FindProperty(propertyName)?.GetColumnName(storeObject) ?? propertyName)
            .ToArray();

        // Both names are kept: the trigger reads the column, while the payload's JSON key is the
        // property name, so a renamed column (HasColumnName, or a naming-convention package) cannot
        // silently unbind the event type the payload is deserialized into.
        var payloadColumns = SplitCsv((string?)entityType.FindAnnotation(NotificationAnnotationNames.PayloadColumns)?.Value)
            .Select(propertyName => new NotificationPayloadColumn(
                propertyName,
                entityType.FindProperty(propertyName)?.GetColumnName(storeObject) ?? propertyName))
            .ToArray();

        return new NotificationEntityConfiguration
        {
            // entityType.ShortName() (derived from the stable metadata Name), not
            // entityType.ClrType.Name: when EF Core reconstructs the "old" side of a migration
            // diff from ModelSnapshot.cs, it builds entity types via the string-named
            // ModelBuilder.Entity(string) overload, which resolves ClrType to a
            // Dictionary<string, object> placeholder rather than the real CLR class. Reading
            // ClrType.Name here would make the fingerprint (and the payload's "entity" field)
            // silently differ between a fresh build and a snapshot-reconstructed one.
            EntityDisplayName = entityType.ShortName(),
            Schema = schema,
            TableName = tableName,
            Operations = operations,
            KeyColumns = keyColumns,
            WatchedUpdateColumns = watchedColumns,
            ChannelStrategy = ResolveChannelStrategy(entityType),
            ChannelNameOverride = (string?)entityType.FindAnnotation(NotificationAnnotationNames.ChannelNameOverride)?.Value,
            PayloadColumns = payloadColumns,
            PayloadBuilder = ResolvePayloadBuilder(entityType),
            PayloadOverflow = ResolvePayloadOverflow(entityType),
            NamePrefix = ResolveNamePrefix(entityType),
        };
    }

    private static string[] SplitCsv(string? raw) =>
        string.IsNullOrEmpty(raw) ? [] : raw.Split(',', StringSplitOptions.RemoveEmptyEntries);

    private static string ResolveNamePrefix(IReadOnlyEntityType entityType) =>
        (string?)entityType.FindAnnotation(NotificationAnnotationNames.NamePrefix)?.Value
        ?? (string?)entityType.Model.FindAnnotation(NotificationAnnotationNames.DefaultNamePrefix)?.Value
        ?? "";

    // Absent on models written before payload overflow was configurable, which must keep the safe
    // behavior rather than the historical one: those triggers could abort a write on a long row.
    private static NotificationPayloadOverflow ResolvePayloadOverflow(IReadOnlyEntityType entityType) =>
        Enum.TryParse<NotificationPayloadOverflow>(
            (string?)entityType.FindAnnotation(NotificationAnnotationNames.PayloadOverflow)?.Value,
            out var overflow)
            ? overflow
            : NotificationPayloadOverflow.Truncate;

    private static INotificationChannelNamingStrategy ResolveChannelStrategy(IReadOnlyEntityType entityType)
    {
        var kind = (string?)entityType.FindAnnotation(NotificationAnnotationNames.ChannelStrategyKind)?.Value
            ?? NotificationChannelStrategyKind.PerEntity;
        var argument = (string?)entityType.FindAnnotation(NotificationAnnotationNames.ChannelStrategyArgument)?.Value;

        return kind switch
        {
            NotificationChannelStrategyKind.Single => new SingleChannelNamingStrategy(
                argument is { Length: > 0 } ? argument : SingleChannelNamingStrategy.DefaultChannelName),
            NotificationChannelStrategyKind.Topic => new TopicChannelNamingStrategy(argument is { Length: > 0 } ? argument : "."),
            NotificationChannelStrategyKind.Custom => CreateCustomInstance<INotificationChannelNamingStrategy>(argument, entityType),
            _ => PerEntityChannelNamingStrategy.Instance,
        };
    }

    private static INotificationPayloadBuilder ResolvePayloadBuilder(IReadOnlyEntityType entityType)
    {
        var kind = (string?)entityType.FindAnnotation(NotificationAnnotationNames.PayloadBuilderKind)?.Value
            ?? NotificationPayloadBuilderKind.Minimal;
        var typeName = (string?)entityType.FindAnnotation(NotificationAnnotationNames.PayloadBuilderTypeName)?.Value;

        return kind switch
        {
            NotificationPayloadBuilderKind.Minimal => MinimalNotificationPayloadBuilder.Instance,
            NotificationPayloadBuilderKind.Projected => ProjectedNotificationPayloadBuilder.Instance,
            NotificationPayloadBuilderKind.Custom => CreateCustomInstance<INotificationPayloadBuilder>(typeName, entityType),
            _ => ExtendedNotificationPayloadBuilder.Instance,
        };
    }

    private static T CreateCustomInstance<T>(string? assemblyQualifiedTypeName, IReadOnlyEntityType entityType)
        where T : class
    {
        if (string.IsNullOrEmpty(assemblyQualifiedTypeName))
        {
            throw new InvalidOperationException(
                $"Entity '{entityType.DisplayName()}' is configured with a custom {typeof(T).Name}, but no type name was recorded.");
        }

        var type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false) ?? throw new InvalidOperationException(
                $"Could not load type '{assemblyQualifiedTypeName}' configured as a custom {typeof(T).Name} for entity '{entityType.DisplayName()}'.");
        try
        {
            return (T)Activator.CreateInstance(type)!;
        }
        catch (Exception ex) when (ex is MissingMethodException or InvalidCastException)
        {
            throw new InvalidOperationException(
                $"Type '{type}' configured as a custom {typeof(T).Name} for entity '{entityType.DisplayName()}' must implement {typeof(T)} and expose a public parameterless constructor.",
                ex);
        }
    }
}
