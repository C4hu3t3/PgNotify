using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.Caching;
using PgNotify.Dispatch;
using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests;

public class ServiceCollectionExtensionsTests
{
    private static IHostedService NotificationHostedService(IServiceProvider provider) =>
        provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "PostgresNotificationHostedService");

    private sealed class DelegateMappingSource(Action<NotificationMappingBuilder> contribute) : INotificationMappingSource
    {
        public Task ContributeAsync(NotificationMappingBuilder builder, CancellationToken cancellationToken)
        {
            contribute(builder);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_missing_connection_string_is_reported_when_the_host_starts()
    {
        // Not at registration: a mapping source may supply one, and may be registered later.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(_ => { });

        await using var provider = services.BuildServiceProvider();
        var act = async () => await NotificationHostedService(provider).StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*connection string*");
    }

    [Fact]
    public async Task A_mapping_source_registered_after_AddPostgresNotifications_is_still_used()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications();

        // Registered afterwards on purpose: this is what an AddDbContext call placed below
        // AddPostgresNotifications looks like from here.
        services.AddSingleton<INotificationMappingSource>(new DelegateMappingSource(b => b
            .MapChannel("orders", nameof(TestOrder), typeof(TestOrder))
            .MapChannel("legacy_audit")
            .UseConnectionString("Host=localhost;Database=from_source")));

        await using var provider = services.BuildServiceProvider();
        var hostedService = NotificationHostedService(provider);
        await hostedService.StartAsync(CancellationToken.None);

        try
        {
            var state = provider.GetRequiredService<NotificationRuntimeState>();
            state.ConnectionString.Should().Be("Host=localhost;Database=from_source;Multiplexing=False;Pooling=False");
            state.Channels.Should().BeEquivalentTo(["orders", "legacy_audit"]);

            provider.GetRequiredService<NotificationChannelMap>().Find(new NotificationEnvelope
            {
                Channel = "orders",
                Entity = nameof(TestOrder),
                Operation = NotificationOperation.Insert,
                Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
                RawPayload = "{}",
            })!.EntityType.Should().Be(typeof(TestOrder));
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task An_explicitly_configured_connection_string_wins_over_a_proposed_one()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=explicit");
        services.AddSingleton<INotificationMappingSource>(new DelegateMappingSource(
            b => b.UseConnectionString("Host=localhost;Database=proposed")));

        await using var provider = services.BuildServiceProvider();
        var hostedService = NotificationHostedService(provider);
        await hostedService.StartAsync(CancellationToken.None);

        try
        {
            provider.GetRequiredService<NotificationRuntimeState>().ConnectionString
                .Should().Be("Host=localhost;Database=explicit;Multiplexing=False;Pooling=False");
        }
        finally
        {
            await hostedService.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void AddPostgresNotifications_registers_the_public_notification_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=test");

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPostgresNotificationService>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddPostgresNotifications_registers_the_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=test");

        // Resolving the hosted service also constructs the listener it depends on, which is
        // only IAsyncDisposable (a LISTEN connection has no synchronous-safe teardown), so the
        // provider itself must be disposed asynchronously here.
        await using var provider = services.BuildServiceProvider();

        // AddHealthChecks() also registers its own publisher hosted service, so assert our
        // service is present rather than that it's the only one.
        provider.GetServices<IHostedService>().Should().ContainSingle(s => s.GetType().Name == "PostgresNotificationHostedService");
    }

    [Fact]
    public void AddPostgresNotifications_wires_up_handlers_discovered_from_the_specified_assembly()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.AddHandlersFromAssembly(typeof(RecordingUserUpdatedHandler).Assembly);
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetServices<IDatabaseUpdatedHandler<TestUser>>().Should().HaveCountGreaterOrEqualTo(2);
        scope.ServiceProvider.GetServices<IDatabaseNotificationHandler<TestUser>>().Should().NotBeEmpty();
        scope.ServiceProvider.GetServices<IDatabaseNotificationHandler>().Should().NotBeEmpty();
    }

    [Fact]
    public void Calling_AddPostgresNotifications_twice_is_rejected()
    {
        // Regression test: every registration in AddPostgresNotifications is a bare AddSingleton
        // with no dedup, so a second call used to add a second PostgresNotificationHostedService
        // sharing the first's listener singleton - both would subscribe to NotificationReceived
        // independently, dispatching every notification twice, silently.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=test");

        var act = () => services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=test");

        act.Should().Throw<InvalidOperationException>().WithMessage("*already called*");
    }

    [Fact]
    public void MapChannel_declares_the_channels_and_the_entity_routing()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.MapChannel<TestUser>("users");
            o.MapChannel("legacy_audit");
        });

        using var provider = services.BuildServiceProvider();
        var map = provider.GetRequiredService<NotificationChannelMap>();

        map.Channels.Should().BeEquivalentTo(["users", "legacy_audit"]);
        map.Find(new NotificationEnvelope
        {
            Channel = "users",
            Entity = nameof(TestUser),
            Operation = NotificationOperation.Update,
            Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
            RawPayload = "{}",
        })!.EntityType.Should().Be(typeof(TestUser));
    }

    [Fact]
    public void MapChannel_rejects_an_empty_channel_name()
    {
        var options = new PostgresNotificationsOptions();

        var mapEntity = () => options.MapChannel<TestUser>("  ");
        var mapBare = () => options.MapChannel("");

        mapEntity.Should().Throw<ArgumentException>();
        mapBare.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddPostgresNotifications_registers_configured_middleware_in_order()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.UseLogging();
            o.UseMetrics();
        });

        using var provider = services.BuildServiceProvider();
        var middlewares = provider.GetServices<INotificationMiddleware>().ToArray();

        middlewares.Should().HaveCount(2);
        middlewares[0].Should().BeOfType<LoggingNotificationMiddleware>();
        middlewares[1].Should().BeOfType<MetricsNotificationMiddleware>();
    }

    [Fact]
    public void AddChangeTracking_registers_the_tracker_services_and_its_middleware()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.AddChangeTracking();
        });

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEntityChangeTrackerSource>().Should().NotBeNull();
        provider.GetRequiredService<IEntityChangeTracker<TestUser>>().Should().NotBeNull();
        provider.GetServices<INotificationMiddleware>().Should().ContainSingle();
    }

    [Fact]
    public void Without_AddChangeTracking_the_tracker_services_are_absent()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost;Database=test");

        using var provider = services.BuildServiceProvider();

        provider.GetService<IEntityChangeTrackerSource>().Should().BeNull();
        provider.GetService<IEntityChangeTracker<TestUser>>().Should().BeNull();
    }

    [Fact]
    public void The_generic_tracker_resolves_to_the_same_instance_as_the_one_keyed_by_entity_name()
    {
        // IEntityChangeTracker<TEntity> is an adapter over the name-keyed registry, so injecting it
        // and looking the entity up by name must observe the same invalidations.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.AddChangeTracking();
        });

        using var provider = services.BuildServiceProvider();
        var byType = provider.GetRequiredService<IEntityChangeTracker<TestUser>>();
        var byName = provider.GetRequiredService<IEntityChangeTrackerSource>().Get(nameof(TestUser));

        var token = byType.GetChangeToken();
        ((EntityChangeTracker)byName).MarkChanged(DateTimeOffset.UtcNow);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void IEntityChangeTrackerSource_Get_by_generic_type_resolves_to_the_same_tracker_as_Get_by_name()
    {
        // Get<TEntity>() is a default interface method that just forwards to Get(string) by CLR
        // type name - EntityChangeTrackerRegistry never overrides it, so this is the only thing
        // that exercises it at all.
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPostgresNotifications(o =>
        {
            o.ConnectionString = "Host=localhost;Database=test";
            o.AddChangeTracking();
        });

        using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IEntityChangeTrackerSource>();

        var byType = source.Get<TestUser>();
        var byName = source.Get(nameof(TestUser));

        var token = byType.GetChangeToken();
        ((EntityChangeTracker)byName).MarkChanged(DateTimeOffset.UtcNow);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void AddChangeTracking_is_idempotent_and_keeps_the_last_coalescing_window()
    {
        var options = new PostgresNotificationsOptions();

        options.AddChangeTracking(TimeSpan.FromMilliseconds(50));
        options.AddChangeTracking(TimeSpan.FromMilliseconds(250));

        options.ChangeTrackingCoalesceWindow.Should().Be(TimeSpan.FromMilliseconds(250));
        options.MiddlewareTypes.Should().ContainSingle();
    }

    [Fact]
    public void AddChangeTracking_rejects_a_negative_coalescing_window()
    {
        var options = new PostgresNotificationsOptions();

        var act = () => options.AddChangeTracking(TimeSpan.FromMilliseconds(-1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
