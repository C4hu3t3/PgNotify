using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PgNotify.EFCore.Tests.TestModels;

public abstract class TestDbContextBase(DbContextOptions options) : DbContext(options);

/// <summary>
/// EF Core caches the compiled model per DbContext-derived type by default, keyed only on the
/// context type (not on per-instance OnModelCreating closures), which would make every test using
/// the same generic test-context type share one stale model. Registering this factory forces a
/// fresh model build for every context instance, which is exactly what independent unit tests need.
/// </summary>
internal sealed class UncachedModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => new object();
}

/// <summary>Fluent-API-only context: exercises HasDatabaseNotifications() directly, no attribute convention wired up.</summary>
public sealed class FluentDbContext(DbContextOptions<FluentDbContext> options) : TestDbContextBase(options)
{
    public Action<ModelBuilder>? ConfigureModel { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel?.Invoke(modelBuilder);

    public static FluentDbContext Create(Action<ModelBuilder> configure)
    {
        var options = new DbContextOptionsBuilder<FluentDbContext>()
            .UseNpgsql("Host=localhost;Database=notifications_efcore_tests")
            .ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>()
            .Options;

        var context = new FluentDbContext(options) { ConfigureModel = configure };
        _ = context.Model; // force model build
        return context;
    }
}

/// <summary>
/// Context with the notification convention set plugin wired up directly (bypassing
/// PgNotify.Migrations' UseNpgsqlNotifications(), which this project does not depend on),
/// so the [NotifyChanges] attribute convention and validation convention run for real.
/// </summary>
public sealed class ConventionDbContext(DbContextOptions<ConventionDbContext> options) : TestDbContextBase(options)
{
    public DbSet<AttributeUser> AttributeUsers => Set<AttributeUser>();
    public DbSet<AttributeInsertOnlyEntity> AttributeInsertOnlyEntities => Set<AttributeInsertOnlyEntity>();
    public DbSet<AttributePrefixedEntity> AttributePrefixedEntities => Set<AttributePrefixedEntity>();

    public Action<ModelBuilder>? ConfigureModel { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel?.Invoke(modelBuilder);

    public static ConventionDbContext Create(Action<ModelBuilder>? configure = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ConventionDbContext>()
            .UseNpgsql("Host=localhost;Database=notifications_efcore_tests")
            .ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>();

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(new TestNotificationsOptionsExtension());

        var context = new ConventionDbContext(optionsBuilder.Options) { ConfigureModel = configure };
        return context;
    }

    public static IModel BuildModel(Action<ModelBuilder>? configure = null)
    {
        using var context = Create(configure);
        return context.Model;
    }
}
