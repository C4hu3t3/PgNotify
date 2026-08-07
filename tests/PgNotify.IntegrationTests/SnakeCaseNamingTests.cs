using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Runs the whole pipeline under EFCore.NamingConventions' snake_case convention, a package a
/// large share of Npgsql users have. It rewrites every convention-derived table and column name,
/// which reaches two different parts of this library: the channel (a table name) and a projected
/// payload's contents (column values).
/// </summary>
/// <remarks>
/// The failure this guards against is silent on both counts — a channel nobody listens to, or JSON
/// keys no event member matches — so only a real server can tell the two sides still agree.
/// </remarks>
public sealed class SnakeCaseNamingTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private string _connectionString = null!;

    private DbContextOptions<SnakeCaseDbContext> Options(bool designTime) =>
        designTime
            ? new DbContextOptionsBuilder<SnakeCaseDbContext>()
                .UseNpgsql(_connectionString)
                .UseNpgsqlNotifications()
                .UseSnakeCaseNamingConvention()
                .Options
            : new DbContextOptionsBuilder<SnakeCaseDbContext>()
                .UseNpgsql(_connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        await using var context = new SnakeCaseDbContext(Options(designTime: true));
        await MigrationApplier.CreateSchemaAsync(context, _connectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private static async Task<SnakeCaseInvoiceInserted?> FirstEventAsync(
        IPostgresNotificationService notifications,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in notifications.Events<SnakeCaseInvoice>(NotificationOperation.Insert, cancellationToken))
        {
            return envelope.ToTyped<SnakeCaseInvoiceInserted>();
        }

        return null;
    }

    [Fact]
    public async Task A_projected_payload_binds_under_snake_case_column_naming()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = _connectionString;
            o.MapChannel<SnakeCaseInvoice>("invoices");
        });

        await using var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitTask = FirstEventAsync(provider.GetRequiredService<IPostgresNotificationService>(), cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        await using var writeContext = new SnakeCaseDbContext(Options(designTime: false));
        var invoice = new SnakeCaseInvoice { InternalNote = "handled by Ada", TotalAmount = 42.75m };
        writeContext.Invoices.Add(invoice);
        await writeContext.SaveChangesAsync();

        SnakeCaseInvoiceInserted? received = null;
        try
        {
            received = await waitTask;
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        received.Should().NotBeNull("the channel is the rewritten table name, and the listener has to agree with the trigger");
        received!.Id.Should().Be(invoice.Id);
        received.InternalNote.Should().Be("handled by Ada", "a snake_case column must still arrive under its property name");
        received.TotalAmount.Should().Be(42.75m);
    }
}
