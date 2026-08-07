using Microsoft.Extensions.DependencyInjection;
using PgNotify.Dispatch;
using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Internal;

public class NotificationDispatchPipelineTests
{
    private sealed class RecordingMiddleware(List<string> log, string name, bool shortCircuit = false) : INotificationMiddleware
    {
        public async Task InvokeAsync(NotificationContext context, NotificationDelegate next)
        {
            log.Add($"{name}:before");
            if (!shortCircuit)
            {
                await next(context).ConfigureAwait(false);
            }

            log.Add($"{name}:after");
        }
    }

    private static NotificationChannelMap UsersMap()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));
        return map;
    }

    private static NotificationContext BuildContext(IServiceProvider services) => new()
    {
        Envelope = new NotificationEnvelope
        {
            Channel = "users",
            Entity = "TestUser",
            Operation = NotificationOperation.Update,
            Keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("""{"id":1}""")!,
            RawPayload = """{"entity":"TestUser","operation":"updated","id":1}""",
        },
        Services = services,
        CancellationToken = CancellationToken.None,
    };

    [Fact]
    public async Task Middlewares_run_in_registration_order_wrapping_the_terminal_dispatcher()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();

        var pipeline = new NotificationDispatchPipeline(
            [new RecordingMiddleware(log, "A"), new RecordingMiddleware(log, "B")],
            UsersMap(),
            new NotificationEventHub());

        await pipeline.InvokeAsync(BuildContext(provider));

        log.Should().Equal("A:before", "B:before", "B:after", "A:after");
    }

    [Fact]
    public async Task A_middleware_that_does_not_call_next_short_circuits_the_pipeline()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        using var provider = services.BuildServiceProvider();

        var pipeline = new NotificationDispatchPipeline(
            [new RecordingMiddleware(log, "A", shortCircuit: true), new RecordingMiddleware(log, "B")],
            UsersMap(),
            new NotificationEventHub());

        using var scope = provider.CreateScope();
        await pipeline.InvokeAsync(BuildContext(scope.ServiceProvider));

        log.Should().Equal("A:before", "A:after");

        var handler = (RecordingUserUpdatedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseUpdatedHandler<TestUser>>();
        handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_middlewares_the_terminal_dispatcher_still_runs()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        using var provider = services.BuildServiceProvider();

        var pipeline = new NotificationDispatchPipeline([], UsersMap(), new NotificationEventHub());

        using var scope = provider.CreateScope();
        await pipeline.InvokeAsync(BuildContext(scope.ServiceProvider));

        var handler = (RecordingUserUpdatedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseUpdatedHandler<TestUser>>();
        handler.Received.Should().ContainSingle();
    }

    [Fact]
    public async Task The_entity_handlers_run_before_the_non_generic_catch_all()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseNotificationHandler>(_ => new RecordingCatchAllHandler(log));
        services.AddScoped<IDatabaseNotificationHandler<TestUser>>(_ => new RecordingUserHandler(log));
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>>(_ => new RecordingUserUpdatedHandler(log));
        using var provider = services.BuildServiceProvider();

        var pipeline = new NotificationDispatchPipeline([], UsersMap(), new NotificationEventHub());

        using var scope = provider.CreateScope();
        await pipeline.InvokeAsync(BuildContext(scope.ServiceProvider));

        // Registered in the opposite order deliberately: the sequence is the pipeline's, not DI's.
        log.Should().Equal("updated", "entity", "catch-all");
    }

    [Fact]
    public async Task The_catch_all_also_receives_notifications_from_a_channel_bound_to_no_entity()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("legacy_audit");

        var services = new ServiceCollection();
        services.AddScoped<IDatabaseNotificationHandler, RecordingCatchAllHandler>();
        using var provider = services.BuildServiceProvider();

        var pipeline = new NotificationDispatchPipeline([], map, new NotificationEventHub());

        using var scope = provider.CreateScope();
        await pipeline.InvokeAsync(new NotificationContext
        {
            Envelope = new NotificationEnvelope
            {
                Channel = "legacy_audit",
                Entity = "AuditEvent",
                Operation = NotificationOperation.Insert,
                Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
                RawPayload = """{"entity":"AuditEvent","operation":"created","id":9}""",
            },
            Services = scope.ServiceProvider,
            CancellationToken = CancellationToken.None,
        });

        var handler = (RecordingCatchAllHandler)scope.ServiceProvider.GetRequiredService<IDatabaseNotificationHandler>();
        handler.Received.Should().ContainSingle(e => e.Entity == "AuditEvent");
    }

    [Fact]
    public async Task Events_subscribers_see_the_notification_before_a_slow_handler_completes()
    {
        // Why the Events<TEntity>() stream is published first in the terminal step and not last:
        // an async-stream consumer must not be made to wait behind an unrelated handler's I/O.
        var handlerGate = new TaskCompletionSource();
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>>(_ => new DelegateUserUpdatedHandler(async _ => await handlerGate.Task));
        using var provider = services.BuildServiceProvider();

        var hub = new NotificationEventHub();
        using var cts = new CancellationTokenSource();
        var enumerator = hub.Subscribe(typeof(TestUser), cts.Token).GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync();

        var pipeline = new NotificationDispatchPipeline([], UsersMap(), hub);
        using var scope = provider.CreateScope();
        var dispatchTask = pipeline.InvokeAsync(BuildContext(scope.ServiceProvider));

        (await moveNextTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        enumerator.Current.Keys["id"].GetInt32().Should().Be(1);
        dispatchTask.IsCompleted.Should().BeFalse("the handler is still awaiting its gate");

        handlerGate.SetResult();
        await dispatchTask;
    }

    [Fact]
    public async Task An_unmapped_channel_reaches_no_entity_handler()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        using var provider = services.BuildServiceProvider();

        // The listener can receive on a channel nothing was mapped to: another process may notify
        // on a channel this one subscribed to for a different entity.
        var pipeline = new NotificationDispatchPipeline([], new NotificationChannelMap(), new NotificationEventHub());

        using var scope = provider.CreateScope();
        await pipeline.InvokeAsync(BuildContext(scope.ServiceProvider));

        var handler = (RecordingUserUpdatedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseUpdatedHandler<TestUser>>();
        handler.Received.Should().BeEmpty();
    }
}
