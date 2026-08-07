using Microsoft.EntityFrameworkCore.Infrastructure;
using PgNotify.Infrastructure;

// Intentionally in the Microsoft.EntityFrameworkCore namespace, alongside EF Core's own DbContext
// extension methods.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Tells whether a context opted into PostgreSQL notifications, which is what makes its model a
/// usable source of channels for the listening side.
/// </summary>
public static class DbContextNotificationExtensions
{
    /// <summary>
    /// Returns <see langword="true"/> if <c>UseNpgsqlNotifications()</c> was chained onto this
    /// context's options.
    /// </summary>
    /// <remarks>
    /// This is the marker, not the configuration: a context can carry it and still have no entity
    /// with notifications enabled. It answers "was this context built to notify", which is the
    /// question a listener asks before reading its model.
    /// </remarks>
    public static bool IsNotificationContext(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<IDbContextOptions>().IsNotificationContext();
    }

    /// <summary>
    /// Returns <see langword="true"/> if <c>UseNpgsqlNotifications()</c> was chained onto
    /// <paramref name="options"/>.
    /// </summary>
    public static bool IsNotificationContext(this IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.FindExtension<NotificationsOptionsExtension>() is not null;
    }

    /// <summary>
    /// Marks this context as a notification source for <c>AddNotificationMappingFromDbContexts()</c>
    /// to discover — the same marker <c>UseNpgsqlNotifications()</c> (from
    /// <c>PgNotify.Migrations</c>) adds, without also replacing this context's migrations SQL
    /// generator. Chain this on a listener-only context that never runs <c>dotnet ef</c> and only
    /// exists to read the model and receive notifications; chain the full
    /// <c>UseNpgsqlNotifications()</c> instead on whichever context owns the schema.
    /// </summary>
    public static DbContextOptionsBuilder UseNpgsqlNotificationsListening(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var extension = optionsBuilder.Options.FindExtension<NotificationsOptionsExtension>() ?? new NotificationsOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return optionsBuilder;
    }

    /// <summary>
    /// Marks this context as a notification source for <c>AddNotificationMappingFromDbContexts()</c>
    /// to discover — see the non-generic overload.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseNpgsqlNotificationsListening<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseNpgsqlNotificationsListening((DbContextOptionsBuilder)optionsBuilder);
}
