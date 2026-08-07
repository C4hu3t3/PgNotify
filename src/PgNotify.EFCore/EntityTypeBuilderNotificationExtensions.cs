using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PgNotify;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, matching where EF Core and
// provider packages place their own fluent configuration entry points (e.g. UseNpgsql,
// HasIndex), so HasDatabaseNotifications() is available anywhere ModelBuilder already is.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Fluent API entry point for configuring PostgreSQL database notifications on an entity.
/// </summary>
public static class EntityTypeBuilderNotificationExtensions
{
    /// <summary>
    /// Enables PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> notifications for this entity. With no
    /// <paramref name="configure"/> callback, defaults to all operations, one channel per
    /// entity, and the minimal payload shape.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasDatabaseNotifications<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        Action<NotificationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);

        var optionsBuilder = new NotificationOptionsBuilder<TEntity>();
        configure?.Invoke(optionsBuilder);
        optionsBuilder.Save(entityTypeBuilder);

        return entityTypeBuilder;
    }
}
