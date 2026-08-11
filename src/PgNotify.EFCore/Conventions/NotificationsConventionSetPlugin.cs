using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace PgNotify.Conventions;

/// <summary>
/// Adds <see cref="NotifyChangesAttributeConvention"/> and <see cref="NotificationValidationConvention"/>
/// to EF Core's convention pipeline. Registered by <c>PgNotify.Migrations</c>'
/// <c>UseNpgsqlNotifications()</c> via <c>IServiceCollection.TryAddEnumerable(...)</c>, which is
/// the correct way for a non-provider EF Core extension package to contribute conventions
/// without displacing conventions registered by other packages. Constructed by EF Core's internal
/// service provider (that is what makes the <see cref="IDiagnosticsLogger{TLoggerCategory}"/>
/// constructor parameter resolvable), the same provider that resolves every other
/// <c>IConventionSetPlugin</c>.
/// </summary>
public sealed class NotificationsConventionSetPlugin(IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    : IConventionSetPlugin
{
    /// <inheritdoc />
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        ArgumentNullException.ThrowIfNull(conventionSet);

        conventionSet.EntityTypeAddedConventions.Add(new NotifyChangesAttributeConvention(logger));
        conventionSet.ModelFinalizingConventions.Add(new NotificationValidationConvention());

        return conventionSet;
    }
}
