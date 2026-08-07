using Microsoft.EntityFrameworkCore;
using PgNotify;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.Serialization;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Regression coverage for <c>json</c>/<c>xml</c> watched columns: PostgreSQL has no <c>=</c> (and
/// therefore no <c>IS DISTINCT FROM</c>) operator for either type at all, so a trigger built from
/// an uncast <c>NEW.col IS DISTINCT FROM OLD.col</c> compiles fine (PL/pgSQL only type-checks an
/// expression the first time it runs) but aborts the whole write transaction the first time an
/// <c>UPDATE</c> actually evaluates it (<c>operator does not exist: json = json</c> /
/// <c>xml = xml</c>) - confirmed against a real PostgreSQL 16 instance before this fix.
/// Goes through the real migrations differ/SQL generator pipeline via <see cref="MigrationApplier"/>,
/// the same path <c>dotnet ef migrations add</c> uses.
/// </summary>
public sealed class NotificationJsonXmlColumnTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<JsonXmlColumnDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new JsonXmlColumnDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<JsonXmlColumnEntity>("json_xml_documents");
        });

        _services = services.BuildServiceProvider();

        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    public async Task DisposeAsync()
    {
        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    private JsonXmlColumnDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<JsonXmlColumnDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task Updating_a_json_column_notifies_without_aborting_the_transaction()
    {
        await using var context = CreateContext();
        var document = new JsonXmlColumnEntity { Metadata = """{"a":1}""", Notes = "<a/>" };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        var waitTask = NotificationWaiter.WaitAsync<JsonXmlColumnEntity>(
            _services.GetRequiredService<IPostgresNotificationService>(),
            NotificationOperation.Update,
            e => EnvelopeId(e) == document.Id);

        document.Metadata = """{"a":2}""";
        var act = () => context.SaveChangesAsync();

        await act.Should().NotThrowAsync("a cast-free IS DISTINCT FROM on json used to abort the write with 'operator does not exist: json = json'");

        var envelope = await waitTask;
        envelope.Changed.Should().Contain("Metadata");
    }

    [Fact]
    public async Task Updating_an_xml_column_notifies_without_aborting_the_transaction()
    {
        await using var context = CreateContext();
        var document = new JsonXmlColumnEntity { Metadata = """{"a":1}""", Notes = "<a/>" };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        var waitTask = NotificationWaiter.WaitAsync<JsonXmlColumnEntity>(
            _services.GetRequiredService<IPostgresNotificationService>(),
            NotificationOperation.Update,
            e => EnvelopeId(e) == document.Id);

        document.Notes = "<b/>";
        var act = () => context.SaveChangesAsync();

        await act.Should().NotThrowAsync("a cast-free IS DISTINCT FROM on xml used to abort the write with 'operator does not exist: xml = xml'");

        var envelope = await waitTask;
        envelope.Changed.Should().Contain("Notes");
    }

    [Fact]
    public async Task Reordering_json_keys_with_the_same_values_does_not_report_the_column_as_changed()
    {
        await using var context = CreateContext();
        var document = new JsonXmlColumnEntity { Metadata = """{"a":1,"b":2}""", Notes = "<a/>" };
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        var waitTask = NotificationWaiter.TryWaitAsync<JsonXmlColumnEntity>(
            _services.GetRequiredService<IPostgresNotificationService>(),
            NotificationOperation.Update,
            e => e.Id() == document.Id,
            NotificationWaiter.AbsenceTimeout);

        // Same JSON value, different key order - the ::jsonb cast compares by parsed value, so this
        // must not be reported as a change.
        document.Metadata = """{"b":2,"a":1}""";
        await context.SaveChangesAsync();

        (await waitTask).Should().BeNull("casting to jsonb before comparison normalizes key order");
    }

    /// <summary>
    /// The extended payload's <c>keys</c> object is keyed by the actual column name ("Id"), unlike
    /// the minimal payload's synthesized top-level "id" field that <c>NotificationWaiter.Id()</c>
    /// reads.
    /// </summary>
    private static int EnvelopeId(NotificationEnvelope envelope) => envelope.Keys["Id"].GetInt32();
}
