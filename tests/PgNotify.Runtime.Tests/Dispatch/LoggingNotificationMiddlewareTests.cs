using Microsoft.Extensions.Logging.Abstractions;
using PgNotify.Dispatch;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Dispatch;

public class LoggingNotificationMiddlewareTests
{
    private static NotificationContext BuildContext() => new()
    {
        Envelope = new NotificationEnvelope
        {
            Channel = "users",
            Entity = "User",
            Operation = NotificationOperation.Update,
            Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
            RawPayload = "{}",
        },
        Services = new ServiceCollectionStub(),
        CancellationToken = CancellationToken.None,
    };

    private sealed class ServiceCollectionStub : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [Fact]
    public async Task InvokeAsync_calls_next_and_completes_successfully()
    {
        var middleware = new LoggingNotificationMiddleware(NullLogger<LoggingNotificationMiddleware>.Instance);
        var nextCalled = false;

        await middleware.InvokeAsync(BuildContext(), _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_rethrows_exceptions_from_next()
    {
        var middleware = new LoggingNotificationMiddleware(NullLogger<LoggingNotificationMiddleware>.Instance);

        var act = () => middleware.InvokeAsync(BuildContext(), _ => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }
}
