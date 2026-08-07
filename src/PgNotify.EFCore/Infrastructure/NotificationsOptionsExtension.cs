using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PgNotify.Conventions;

namespace PgNotify.Infrastructure;

/// <summary>
/// The <see cref="IDbContextOptionsExtension"/> registered by <c>UseNpgsqlNotifications()</c>.
/// Its only job is to add <see cref="NotificationsConventionSetPlugin"/> to the model-building
/// pipeline via <c>TryAddEnumerable</c> — the correct way for an EF Core extension package to
/// contribute a convention without displacing conventions added by other packages. The
/// annotation-provider and SQL-generator overrides are wired up separately via
/// <c>DbContextOptionsBuilder.ReplaceService</c>, EF Core's supported mechanism for overriding
/// single-registration provider services.
/// </summary>
/// <remarks>
/// It lives here rather than in <c>PgNotify.Migrations</c>, which is what registers it,
/// because it doubles as the marker "this context opted into notifications" — and the listening
/// side reads that marker (see <c>DbContextNotificationExtensions.IsNotificationContext</c>) to
/// derive what to <c>LISTEN</c> on. Reading it from <c>PgNotify.Migrations</c> would make a
/// listener process reference the whole Npgsql EF Core provider to answer a yes/no question.
/// </remarks>
internal sealed class NotificationsOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConventionSetPlugin, NotificationsConventionSetPlugin>());

    public void Validate(IDbContextOptions options)
    {
    }

    public IDbContextOptionsExtension ApplyDefaults(IDbContextOptions options) => this;

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using PostgreSQL notifications ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Notifications:Enabled"] = "1";
    }
}
