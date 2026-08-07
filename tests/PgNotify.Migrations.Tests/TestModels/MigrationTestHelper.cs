using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PgNotify.Migrations.Tests.TestModels;

internal sealed class UncachedModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => new object();
}

public sealed class NotificationsTestDbContext(DbContextOptions<NotificationsTestDbContext> options) : DbContext(options)
{
    public Action<ModelBuilder>? ConfigureModel { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel?.Invoke(modelBuilder);
}

public static class MigrationTestHelper
{
    private const string ConnectionString = "Host=localhost;Database=notifications_migrations_tests";

    public static NotificationsTestDbContext CreateContext(Action<ModelBuilder> configureModel)
    {
        var options = new DbContextOptionsBuilder<NotificationsTestDbContext>()
            .UseNpgsql(ConnectionString)
            .UseNpgsqlNotifications()
            .ReplaceService<IModelCacheKeyFactory, UncachedModelCacheKeyFactory>()
            .Options;

        return new NotificationsTestDbContext(options) { ConfigureModel = configureModel };
    }

    /// <summary>Generates the SQL for creating the model from scratch (no prior model).</summary>
    public static IReadOnlyList<string> GenerateCreateSql(Action<ModelBuilder> configureModel) =>
        GenerateDiffSql(_ => { }, configureModel);

    /// <summary>Generates the SQL for the diff between two model states.</summary>
    public static IReadOnlyList<string> GenerateDiffSql(Action<ModelBuilder> before, Action<ModelBuilder> after)
    {
        using var beforeContext = CreateContext(before);
        using var afterContext = CreateContext(after);

        var sourceModel = beforeContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var targetModel = afterContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();

        var differ = afterContext.GetService<IMigrationsModelDiffer>();
        var operations = differ.GetDifferences(sourceModel, targetModel);

        var sqlGenerator = afterContext.GetService<IMigrationsSqlGenerator>();
        var commands = sqlGenerator.Generate(operations, afterContext.GetService<IDesignTimeModel>().Model);

        return commands.Select(c => c.CommandText).ToArray();
    }

    /// <summary>Generates the SQL for the diff when dropping tables between two model states (target may omit entities present in source).</summary>
    public static IReadOnlyList<string> GenerateDropSql(Action<ModelBuilder> before) =>
        GenerateDiffSql(before, _ => { });
}
