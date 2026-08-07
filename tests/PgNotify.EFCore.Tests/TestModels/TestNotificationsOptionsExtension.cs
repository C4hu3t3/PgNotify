using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using PgNotify.Conventions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PgNotify.EFCore.Tests.TestModels;

/// <summary>
/// Minimal stand-in for what <c>PgNotify.Migrations</c>' real <c>UseNpgsqlNotifications()</c>
/// will register (this test project intentionally has no dependency on Migrations). Registers
/// <see cref="NotificationsConventionSetPlugin"/> the correct way for an EF Core extension
/// package: via <c>TryAddEnumerable</c> in <see cref="ApplyServices"/>, not <c>ReplaceService</c>
/// (which does not compose correctly with the multi-registration <c>IConventionSetPlugin</c> service).
/// </summary>
internal sealed class TestNotificationsOptionsExtension : IDbContextOptionsExtension
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

        public override string LogFragment => "using PgNotify-test-plugin ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["Notifications:TestPlugin"] = "1";
    }
}
