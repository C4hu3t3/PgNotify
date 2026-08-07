using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PgNotify.Caching;
using PgNotify.Dispatch;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Caching;

public class ChangeTrackingNotificationMiddlewareTests
{
    private static NotificationContext BuildContext(string entity, DateTimeOffset? timestamp = null) => new()
    {
        Envelope = new NotificationEnvelope
        {
            Channel = "users",
            Entity = entity,
            Operation = NotificationOperation.Update,
            Keys = new Dictionary<string, JsonElement>(),
            Timestamp = timestamp,
            RawPayload = """{"entity":"User","operation":"updated","id":1}""",
        },
        Services = new ServiceCollection().BuildServiceProvider(),
        CancellationToken = CancellationToken.None,
    };

    private static EntityChangeTrackerRegistry CreateRegistry() => new(TimeSpan.Zero, NullLogger<EntityChangeTrackerRegistry>.Instance);

    [Fact]
    public async Task A_notification_invalidates_the_tracker_for_its_entity()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var token = registry.Get("User").GetChangeToken();

        await middleware.InvokeAsync(BuildContext("User"), _ => Task.CompletedTask);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Trackers_for_other_entities_are_left_alone()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var orderToken = registry.Get("Order").GetChangeToken();

        await middleware.InvokeAsync(BuildContext("User"), _ => Task.CompletedTask);

        orderToken.HasChanged.Should().BeFalse();
    }

    [Fact]
    public async Task The_payload_timestamp_becomes_LastModified()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var triggerTime = DateTimeOffset.UtcNow.AddMinutes(1);

        await middleware.InvokeAsync(BuildContext("User", triggerTime), _ => Task.CompletedTask);

        registry.Get("User").LastModified.Should().Be(triggerTime.ToUniversalTime());
    }

    [Fact]
    public async Task A_payload_without_a_timestamp_falls_back_to_receive_time()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var before = DateTimeOffset.UtcNow;

        await middleware.InvokeAsync(BuildContext("User"), _ => Task.CompletedTask);

        registry.Get("User").LastModified.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task The_rest_of_the_pipeline_still_runs()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var nextCalled = false;

        await middleware.InvokeAsync(BuildContext("User"), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_handler_downstream_still_leaves_the_tracker_invalidated()
    {
        using var registry = CreateRegistry();
        var middleware = new ChangeTrackingNotificationMiddleware(registry);
        var token = registry.Get("User").GetChangeToken();

        var act = async () => await middleware.InvokeAsync(
            BuildContext("User"),
            _ => Task.FromException(new InvalidOperationException("handler failed")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void The_same_tracker_instance_is_returned_for_the_same_entity()
    {
        using var registry = CreateRegistry();

        registry.Get("User").Should().BeSameAs(registry.Get("User"));
    }
}
