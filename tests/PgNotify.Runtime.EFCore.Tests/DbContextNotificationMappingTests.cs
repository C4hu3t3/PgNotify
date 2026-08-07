using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Internal;
using PgNotify.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PgNotify.Runtime.EFCore.Tests.TestModels;
using Npgsql;

namespace PgNotify.Runtime.EFCore.Tests;

/// <summary>
/// The mapping is derived from models, never from a connection: every context here points at a
/// host that does not resolve, and nothing in these tests opens a socket.
/// </summary>
public class DbContextNotificationMappingTests
{
    private const string ProductConnectionString = "Host=unroutable.invalid;Database=products;Username=u;Password=p";
    private const string OrderConnectionString = "Host=unroutable.invalid;Database=orders;Username=u;Password=p";

    private static NotificationEnvelope Envelope(string channel, string entity) => new()
    {
        Channel = channel,
        Entity = entity,
        Operation = NotificationOperation.Insert,
        Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
        RawPayload = "{}",
    };

    private static async Task<ServiceProvider> StartAsync(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);

        var provider = services.BuildServiceProvider();
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }

        return provider;
    }

    private static async Task StopAsync(ServiceProvider provider)
    {
        foreach (var hostedService in provider.GetServices<IHostedService>())
        {
            await hostedService.StopAsync(CancellationToken.None);
        }

        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Discovery_derives_the_channel_and_the_entity_type_from_a_marked_context()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ProductContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());

            // Registered before the context on purpose: the mapping is read when the host starts,
            // so its registration order relative to AddDbContext must not matter.
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            var state = provider.GetRequiredService<NotificationRuntimeState>();
            state.Channels.Should().BeEquivalentTo(["products"]);

            provider.GetRequiredService<NotificationChannelMap>()
                .Find(Envelope("products", nameof(Product)))!.EntityType.Should().Be(typeof(Product));
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task Discovery_derives_the_channel_from_a_context_marked_via_UseNpgsqlNotificationsListening()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ListeningOnlyContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotificationsListening());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            var state = provider.GetRequiredService<NotificationRuntimeState>();
            state.Channels.Should().BeEquivalentTo(["products"]);

            provider.GetRequiredService<NotificationChannelMap>()
                .Find(Envelope("products", nameof(Product)))!.EntityType.Should().Be(typeof(Product));
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task An_entity_publishing_on_one_channel_per_operation_contributes_all_of_them()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<TopicOrderContext>(o => o.UseNpgsql(OrderConnectionString).UseNpgsqlNotifications());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            provider.GetRequiredService<NotificationRuntimeState>().Channels
                .Should().BeEquivalentTo(["order.created", "order.updated", "order.deleted"]);

            var map = provider.GetRequiredService<NotificationChannelMap>();
            map.Find(Envelope("order.created", nameof(Order)))!.EntityType.Should().Be(typeof(Order));
            map.Find(Envelope("order.deleted", nameof(Order)))!.EntityType.Should().Be(typeof(Order));
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task Several_marked_contexts_are_merged()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ProductContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
            services.AddDbContext<OrderContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            provider.GetRequiredService<NotificationRuntimeState>().Channels
                .Should().BeEquivalentTo(["products", "orders"]);
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task A_context_that_did_not_opt_in_contributes_nothing()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<UnmarkedContext>(o => o.UseNpgsql(ProductConnectionString));
            services.AddDbContext<OrderContext>(o => o.UseNpgsql(OrderConnectionString).UseNpgsqlNotifications());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            provider.GetRequiredService<NotificationRuntimeState>().Channels.Should().BeEquivalentTo(["orders"]);
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task Naming_a_context_that_did_not_opt_in_is_an_error()
    {
        // Skipping it silently is right for discovery and wrong here: the caller asked for this
        // context's channels by name and would receive none, with nothing to read.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<UnmarkedContext>(o => o.UseNpgsql(ProductConnectionString));
        services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContext<UnmarkedContext>());

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "PostgresNotificationHostedService");

        var act = async () => await hostedService.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{nameof(UnmarkedContext)}*UseNpgsqlNotifications*");
    }

    [Fact]
    public async Task Naming_one_context_ignores_the_others()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ProductContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
            services.AddDbContext<OrderContext>(o => o.UseNpgsql(OrderConnectionString).UseNpgsqlNotifications());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContext<OrderContext>());
        });

        try
        {
            var state = provider.GetRequiredService<NotificationRuntimeState>();
            state.Channels.Should().BeEquivalentTo(["orders"]);
            new NpgsqlConnectionStringBuilder(state.ConnectionString).Database.Should().Be("orders");
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task The_inherited_connection_string_is_adjusted_for_a_LISTEN_connection()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ProductContext>(o => o
                .UseNpgsql($"{ProductConnectionString};Multiplexing=true;Pooling=true")
                .UseNpgsqlNotifications());
            services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());
        });

        try
        {
            var resolved = new NpgsqlConnectionStringBuilder(
                provider.GetRequiredService<NotificationRuntimeState>().ConnectionString);

            resolved.Multiplexing.Should().BeFalse();
            resolved.Pooling.Should().BeFalse();
            resolved.Database.Should().Be("products");
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task An_explicitly_configured_connection_string_still_wins()
    {
        var provider = await StartAsync(services =>
        {
            services.AddDbContext<ProductContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
            services.AddPostgresNotifications(o =>
            {
                o.ConnectionString = "Host=unroutable.invalid;Database=explicit";
                o.AddNotificationMappingFromDbContexts();
            });
        });

        try
        {
            new NpgsqlConnectionStringBuilder(provider.GetRequiredService<NotificationRuntimeState>().ConnectionString)
                .Database.Should().Be("explicit");
        }
        finally
        {
            await StopAsync(provider);
        }
    }

    [Fact]
    public async Task Two_entities_whose_names_collide_on_one_channel_are_reported()
    {
        // Discovery makes this reachable without anyone choosing it: a shared channel plus two
        // same-named types in different namespaces, and the payload carries the short name only.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CollidingNamesContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
        services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "PostgresNotificationHostedService");

        var act = async () => await hostedService.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*app_events*Invoice*");
    }

    [Fact]
    public async Task Contexts_pointing_at_different_databases_are_reported_rather_than_arbitrated()
    {
        // Picking one would send the listener to a database nobody chose, and the symptom - one
        // context's notifications simply never arriving - looks like anything but this.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ProductContext>(o => o.UseNpgsql(ProductConnectionString).UseNpgsqlNotifications());
        services.AddDbContext<OrderContext>(o => o.UseNpgsql(OrderConnectionString).UseNpgsqlNotifications());
        services.AddPostgresNotifications(options => options.AddNotificationMappingFromDbContexts());

        await using var provider = services.BuildServiceProvider();
        var hostedService = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "PostgresNotificationHostedService");

        var act = async () => await hostedService.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 different connection strings*");
    }
}
