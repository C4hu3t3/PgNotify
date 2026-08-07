using Microsoft.Extensions.DependencyInjection;
using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Internal;

public class EntityNotificationDispatcherTests
{
    private static NotificationEnvelope Envelope(NotificationOperation operation, int id = 42) => new()
    {
        Channel = "users",
        Entity = "TestUser",
        Operation = operation,
        Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
        RawPayload = $$"""{"entity":"TestUser","operation":"{{operation.ToPastTenseWord()}}","id":{{id}}}""",
    };

    [Fact]
    public async Task DispatchAsync_invokes_every_handler_registered_for_the_operation()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, SecondUserUpdatedHandler>();
        using var provider = services.BuildServiceProvider();

        SecondUserUpdatedHandler.InvocationCount = 0;
        using var scope = provider.CreateScope();

        await new EntityNotificationDispatcher<TestUser>()
            .DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);

        var recording = scope.ServiceProvider.GetServices<IDatabaseUpdatedHandler<TestUser>>()
            .OfType<RecordingUserUpdatedHandler>().Single();

        recording.Received.Should().ContainSingle(e => e.Operation == NotificationOperation.Update);
        SecondUserUpdatedHandler.InvocationCount.Should().Be(1);
    }

    [Theory]
    [InlineData(NotificationOperation.Insert)]
    [InlineData(NotificationOperation.Update)]
    [InlineData(NotificationOperation.Delete)]
    public async Task Only_the_group_matching_the_envelopes_operation_runs(NotificationOperation operation)
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseInsertedHandler<TestUser>, RecordingUserInsertedHandler>();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        services.AddScoped<IDatabaseDeletedHandler<TestUser>, RecordingUserDeletedHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await new EntityNotificationDispatcher<TestUser>()
            .DispatchAsync(Envelope(operation), scope.ServiceProvider, CancellationToken.None);

        var inserted = (RecordingUserInsertedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseInsertedHandler<TestUser>>();
        var updated = (RecordingUserUpdatedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseUpdatedHandler<TestUser>>();
        var deleted = (RecordingUserDeletedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseDeletedHandler<TestUser>>();

        inserted.Received.Should().HaveCount(operation == NotificationOperation.Insert ? 1 : 0);
        updated.Received.Should().HaveCount(operation == NotificationOperation.Update ? 1 : 0);
        deleted.Received.Should().HaveCount(operation == NotificationOperation.Delete ? 1 : 0);
    }

    [Fact]
    public async Task The_operation_specific_group_runs_before_the_entity_wide_one()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseNotificationHandler<TestUser>>(_ => new RecordingUserHandler(log));
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>>(_ => new RecordingUserUpdatedHandler(log));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await new EntityNotificationDispatcher<TestUser>()
            .DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);

        // Registered entity-wide first, on purpose: the order is the dispatcher's, not DI's.
        log.Should().Equal("updated", "entity");
    }

    [Fact]
    public async Task The_entity_wide_group_runs_for_every_operation()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseNotificationHandler<TestUser>, RecordingUserHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dispatcher = new EntityNotificationDispatcher<TestUser>();

        await dispatcher.DispatchAsync(Envelope(NotificationOperation.Insert), scope.ServiceProvider, CancellationToken.None);
        await dispatcher.DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);
        await dispatcher.DispatchAsync(Envelope(NotificationOperation.Delete), scope.ServiceProvider, CancellationToken.None);

        var handler = (RecordingUserHandler)scope.ServiceProvider.GetRequiredService<IDatabaseNotificationHandler<TestUser>>();
        handler.Received.Select(e => e.Operation).Should()
            .Equal(NotificationOperation.Insert, NotificationOperation.Update, NotificationOperation.Delete);
    }

    [Fact]
    public async Task Handlers_registered_for_another_entity_are_never_resolved()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>, RecordingUserUpdatedHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        await new EntityNotificationDispatcher<TestOrder>()
            .DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);

        var handler = (RecordingUserUpdatedHandler)scope.ServiceProvider.GetRequiredService<IDatabaseUpdatedHandler<TestUser>>();
        handler.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_awaits_each_handler_before_invoking_the_next()
    {
        var order = new List<string>();
        var firstHandlerGate = new TaskCompletionSource();
        var services = new ServiceCollection();
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>>(_ => new DelegateUserUpdatedHandler(async _ =>
        {
            order.Add("first:start");
            await firstHandlerGate.Task;
            order.Add("first:end");
        }));
        services.AddScoped<IDatabaseUpdatedHandler<TestUser>>(_ => new DelegateUserUpdatedHandler(_ =>
        {
            order.Add("second");
            return Task.CompletedTask;
        }));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatchTask = new EntityNotificationDispatcher<TestUser>()
            .DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);

        // Execution runs synchronously up to the first incomplete await, so by the time
        // DispatchAsync has returned its (still-pending) Task, the first handler has started but
        // the second has not - it only runs once the first one's returned Task completes.
        order.Should().Equal("first:start");
        dispatchTask.IsCompleted.Should().BeFalse();

        firstHandlerGate.SetResult();
        await dispatchTask;

        order.Should().Equal("first:start", "first:end", "second");
    }

    [Fact]
    public async Task DispatchAsync_with_no_registered_handlers_does_not_throw()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var act = () => new EntityNotificationDispatcher<TestUser>()
            .DispatchAsync(Envelope(NotificationOperation.Update), scope.ServiceProvider, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
