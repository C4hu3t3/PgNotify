using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using PgNotify.Migrations;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, matching where UseNpgsql() and
// other provider/extension entry points live.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Enables PostgreSQL database notification migrations support: entities configured with
/// <c>HasDatabaseNotifications()</c> or <c>[NotifyChanges]</c> automatically get trigger/trigger
/// function DDL generated as part of <c>dotnet ef migrations add</c>.
/// </summary>
public static class DbContextOptionsBuilderNotificationsExtensions
{
    /// <summary>
    /// Enables PostgreSQL database notifications for this context. Chain after <c>UseNpgsql(...)</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseNpgsqlNotifications(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Adds the same marker UseNpgsqlNotificationsListening() does; this method additionally
        // wires up migrations SQL generation, which only makes sense for a context that owns the
        // schema — see that method for the listener-only counterpart.
        optionsBuilder.UseNpgsqlNotificationsListening();

        optionsBuilder.ReplaceService<IRelationalAnnotationProvider, NpgsqlNotificationsAnnotationProvider>();
        optionsBuilder.ReplaceService<IMigrationsAnnotationProvider, NpgsqlNotificationsMigrationsAnnotationProvider>();
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, NpgsqlNotificationsMigrationsSqlGenerator>();

        return optionsBuilder;
    }

    /// <summary>
    /// Enables PostgreSQL database notifications for this context. Chain after <c>UseNpgsql(...)</c>.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseNpgsqlNotifications<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseNpgsqlNotifications((DbContextOptionsBuilder)optionsBuilder);
}
