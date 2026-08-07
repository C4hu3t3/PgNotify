using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.IntegrationTests.TestModels;
using Testcontainers.PostgreSql;

namespace PgNotify.IntegrationTests;

/// <summary>
/// An entity configured only by <c>[NotifyChanges]</c>, end to end. Every other integration test
/// configures its entities fluently, so until the attribute gained the fluent API's options no
/// attribute-written annotation had ever reached a real trigger: the watched-property filter, the
/// projected payload and the topic channel names are all decided from attribute arguments here.
/// </summary>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class AttributeConfiguredEntityTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private ServiceProvider _services = null!;
    private string _connectionString = null!;

    private static readonly ConcurrentQueue<NotificationEnvelope> Received = new();
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        Received.Clear();
        _armed = true;

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        var contextOptions = new DbContextOptionsBuilder<AttributeTicketDbContext>()
            .UseNpgsql(_connectionString)
            .UseNpgsqlNotifications()
            .Options;

        using (var context = new AttributeTicketDbContext(contextOptions))
        {
            await MigrationApplier.CreateSchemaAsync(context, _connectionString);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AttributeTicketDbContext>(o => o.UseNpgsql(_connectionString).UseNpgsqlNotifications());
        services.AddPostgresNotifications(o =>
        {
            o.AddHandlersFromAssembly(typeof(TicketHandler).Assembly);
            o.AddNotificationMappingFromDbContexts();
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
        _armed = false;

        foreach (var hostedService in _services.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await _services.DisposeAsync();
        await _container.DisposeAsync();
    }

    private sealed class TicketHandler : IDatabaseUpdatedHandler<AttributeTicket>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (_armed)
            {
                Received.Enqueue(envelope);
            }

            return Task.CompletedTask;
        }
    }

    private sealed record TicketChanged(int Id, string Status, string Title);

    private static async Task<NotificationEnvelope?> WaitForOneAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Received.TryPeek(out var envelope))
            {
                return envelope;
            }

            await Task.Delay(50);
        }

        return null;
    }

    [Fact]
    public async Task The_attributes_channel_watched_property_and_projected_payload_all_take_effect()
    {
        var options = new DbContextOptionsBuilder<AttributeTicketDbContext>().UseNpgsql(_connectionString).Options;
        await using var context = new AttributeTicketDbContext(options);

        var ticket = new AttributeTicket { Title = "Disk full", Status = "open", InternalNote = "secret" };
        context.Tickets.Add(ticket);
        await context.SaveChangesAsync();

        // Not a watched property: WatchedProperties = [Status] must keep this update quiet.
        ticket.InternalNote = "still secret";
        await context.SaveChangesAsync();
        (await WaitForOneAsync(TimeSpan.FromSeconds(2))).Should().BeNull("only Status is watched");

        ticket.Status = "closed";
        await context.SaveChangesAsync();

        var envelope = await WaitForOneAsync(TimeSpan.FromSeconds(10));
        envelope.Should().NotBeNull();

        // ChannelStrategy = Topic with ChannelArgument = "-".
        envelope!.Channel.Should().Be("attributeticket-updated");
        envelope.Entity.Should().Be(nameof(AttributeTicket));

        // PayloadProperties = [Status, Title], plus the key, and nothing else.
        var typed = envelope.ToTyped<TicketChanged>();
        typed.Id.Should().Be(ticket.Id);
        typed.Status.Should().Be("closed");
        typed.Title.Should().Be("Disk full");
        envelope.RawPayload.Should().NotContain("secret");
    }
}
