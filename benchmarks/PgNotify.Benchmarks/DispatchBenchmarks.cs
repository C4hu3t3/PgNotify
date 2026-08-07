using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using PgNotify.Internal;
using PgNotify.Serialization;

namespace PgNotify.Benchmarks;

/// <summary>
/// Exercises the per-notification hot path: the routing lookup
/// (<see cref="NotificationChannelMap.Find"/>, a dictionary read) and the dispatcher behind it
/// (<see cref="EntityNotificationDispatcher{TEntity}"/>, which resolves handlers from the scope).
/// </summary>
/// <remarks>
/// <c>DispatchWithTwoHandlers</c> against <c>DispatchWithNoHandlers</c> is the check the routing
/// refactor needs: resolving two handler groups per notification — the operation-specific one and
/// the entity-wide one — must cost nothing when nobody registered them.
/// </remarks>
[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private static readonly NotificationEnvelope Envelope = new()
    {
        Channel = "users",
        Entity = "User",
        Operation = NotificationOperation.Update,
        Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
        RawPayload = """{"entity":"User","operation":"updated","id":42,"name":"Ada Lovelace"}""",
    };

    private NotificationChannelMap _channelMap = null!;
    private ServiceProvider _noHandlersProvider = null!;
    private ServiceProvider _twoHandlersProvider = null!;
    private ServiceProvider _bothGroupsProvider = null!;
    private IServiceScope _noHandlersScope = null!;
    private IServiceScope _twoHandlersScope = null!;
    private IServiceScope _bothGroupsScope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _channelMap = new NotificationChannelMap();
        _channelMap.MapChannel("users", typeof(User));
        _channelMap.MapChannel("orders", typeof(Order));
        _channelMap.MapChannel("invoices", typeof(Invoice));
        _channelMap.MapChannel("products", typeof(Product));

        var noHandlers = new ServiceCollection();
        _noHandlersProvider = noHandlers.BuildServiceProvider();
        _noHandlersScope = _noHandlersProvider.CreateScope();

        var twoHandlers = new ServiceCollection();
        twoHandlers.AddScoped<IDatabaseUpdatedHandler<User>, NoOpUserUpdatedHandler>();
        twoHandlers.AddScoped<IDatabaseUpdatedHandler<User>, SecondNoOpUserUpdatedHandler>();
        _twoHandlersProvider = twoHandlers.BuildServiceProvider();
        _twoHandlersScope = _twoHandlersProvider.CreateScope();

        var bothGroups = new ServiceCollection();
        bothGroups.AddScoped<IDatabaseUpdatedHandler<User>, NoOpUserUpdatedHandler>();
        bothGroups.AddScoped<IDatabaseUpdatedHandler<User>, SecondNoOpUserUpdatedHandler>();
        bothGroups.AddScoped<IDatabaseNotificationHandler<User>, NoOpUserHandler>();
        _bothGroupsProvider = bothGroups.BuildServiceProvider();
        _bothGroupsScope = _bothGroupsProvider.CreateScope();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _noHandlersScope.Dispose();
        _twoHandlersScope.Dispose();
        _bothGroupsScope.Dispose();
        _noHandlersProvider.Dispose();
        _twoHandlersProvider.Dispose();
        _bothGroupsProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    public object? ChannelMapLookup() => _channelMap.Find(Envelope);

    [Benchmark]
    public Task DispatchWithNoHandlers()
    {
        var dispatcher = _channelMap.Find(Envelope)!;
        return dispatcher.DispatchAsync(Envelope, _noHandlersScope.ServiceProvider, CancellationToken.None);
    }

    [Benchmark]
    public Task DispatchWithTwoHandlers()
    {
        var dispatcher = _channelMap.Find(Envelope)!;
        return dispatcher.DispatchAsync(Envelope, _twoHandlersScope.ServiceProvider, CancellationToken.None);
    }

    [Benchmark]
    public Task DispatchWithBothHandlerGroups()
    {
        var dispatcher = _channelMap.Find(Envelope)!;
        return dispatcher.DispatchAsync(Envelope, _bothGroupsScope.ServiceProvider, CancellationToken.None);
    }
}
