using PgNotify.Metadata;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, alongside HasDatabaseNotifications()
// and other EF Core model-wide configuration (e.g. HasDefaultSchema).
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Model-wide configuration for PostgreSQL database notifications.
/// </summary>
public static class ModelBuilderNotificationExtensions
{
    /// <summary>
    /// Sets the default trigger/function name prefix for every entity that doesn't set its own
    /// via <c>HasDatabaseNotifications(o =&gt; o.WithNamePrefix(...))</c> or
    /// <c>[NotifyChanges(NamePrefix = ...)]</c>. Useful to make every generated object in this
    /// context unambiguous and collision-free (e.g. <c>"myapp_"</c>) without repeating it per entity.
    /// </summary>
    public static ModelBuilder HasNotificationNamePrefix(this ModelBuilder modelBuilder, string prefix)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(prefix);

        modelBuilder.Model.SetAnnotation(NotificationAnnotationNames.DefaultNamePrefix, prefix);
        return modelBuilder;
    }
}
