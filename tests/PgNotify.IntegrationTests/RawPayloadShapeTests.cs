using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PgNotify.IntegrationTests.TestModels;
using Npgsql;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Verifies the raw JSON payload PostgreSQL sends over the wire matches the documented extended
/// payload shape, using a plain <see cref="NpgsqlConnection"/> LISTEN (no PgNotify.Runtime
/// involved) — this is purely about proving the generated trigger SQL is correct.
/// </summary>
public sealed class RawPayloadShapeTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var options = new DbContextOptionsBuilder<ProductDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using var context = new ProductDbContext(options);
        await MigrationApplier.CreateSchemaAsync(context, _connectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Extended_payload_trigger_produces_the_documented_json_shape()
    {
        await using var listenerConnection = new NpgsqlConnection(_connectionString);
        await listenerConnection.OpenAsync();

        string? payload = null;
        listenerConnection.Notification += (_, e) => payload = e.Payload;

        await using (var listenCommand = listenerConnection.CreateCommand())
        {
            listenCommand.CommandText = "LISTEN test_products;";
            await listenCommand.ExecuteNonQueryAsync();
        }

        await using var writeConnection = new NpgsqlConnection(_connectionString);
        await writeConnection.OpenAsync();
        await using (var insertCommand = writeConnection.CreateCommand())
        {
            insertCommand.CommandText = """INSERT INTO test_products ("Name") VALUES ('Widget');""";
            await insertCommand.ExecuteNonQueryAsync();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (payload is null && !cts.IsCancellationRequested)
        {
            await listenerConnection.WaitAsync(cts.Token);
        }

        payload.Should().NotBeNull();

        using var document = JsonDocument.Parse(payload!);
        var root = document.RootElement;

        root.GetProperty("entity").GetString().Should().Be("TestProduct");
        root.GetProperty("schema").GetString().Should().Be("");
        root.GetProperty("table").GetString().Should().Be("test_products");
        root.GetProperty("operation").GetString().Should().Be("created");
        root.GetProperty("keys").GetProperty("Id").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("changed").GetArrayLength().Should().Be(0);
        root.TryGetProperty("timestamp", out _).Should().BeTrue();
    }
}
