using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Applies a DbContext's model to a real database by driving the same
/// IMigrationsModelDiffer + IMigrationsSqlGenerator pipeline <c>dotnet ef migrations add</c>
/// uses (see PgNotify.Migrations.Tests for the pattern), then executing the generated SQL
/// directly. This proves the generated trigger/function SQL actually runs on PostgreSQL, without
/// requiring the dotnet-ef design-time tool or checked-in migration files for a test project.
/// </summary>
internal static class MigrationApplier
{
    /// <summary>Applies <paramref name="context"/>'s model to an empty database.</summary>
    public static Task CreateSchemaAsync(DbContext context, string connectionString, CancellationToken cancellationToken = default) =>
        ApplyAsync(sourceModel: null, target: context, connectionString, cancellationToken);

    /// <summary>
    /// Applies the diff between two models, the way a second <c>dotnet ef migrations add</c> would
    /// — the only way to exercise what happens to an already-deployed trigger when the model
    /// evolves.
    /// </summary>
    public static Task ApplyDiffAsync(
        DbContext from,
        DbContext to,
        string connectionString,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(from.GetService<IDesignTimeModel>().Model.GetRelationalModel(), to, connectionString, cancellationToken);

    private static async Task ApplyAsync(
        IRelationalModel? sourceModel,
        DbContext target,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var targetModel = target.GetService<IDesignTimeModel>().Model;
        var differ = target.GetService<IMigrationsModelDiffer>();
        var sqlGenerator = target.GetService<IMigrationsSqlGenerator>();

        var operations = differ.GetDifferences(sourceModel!, targetModel.GetRelationalModel());
        var commands = sqlGenerator.Generate(operations, targetModel);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var command in commands)
        {
            await using var npgsqlCommand = connection.CreateCommand();
            npgsqlCommand.CommandText = command.CommandText;
            await npgsqlCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
