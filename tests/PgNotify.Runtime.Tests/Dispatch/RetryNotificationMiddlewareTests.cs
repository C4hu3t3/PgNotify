using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PgNotify.Dispatch;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Dispatch;

public class RetryNotificationMiddlewareTests
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
        Services = new NoopServiceProvider(),
        CancellationToken = CancellationToken.None,
    };

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static RetryNotificationMiddleware CreateMiddleware(int maxAttempts) =>
        new(
            Options.Create(new PostgresNotificationsOptions { MaxRetryAttempts = maxAttempts }),
            NullLogger<RetryNotificationMiddleware>.Instance);

    [Fact]
    public async Task InvokeAsync_succeeds_immediately_when_next_does_not_throw()
    {
        var middleware = CreateMiddleware(maxAttempts: 3);
        var attempts = 0;

        await middleware.InvokeAsync(BuildContext(), _ =>
        {
            attempts++;
            return Task.CompletedTask;
        });

        attempts.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_retries_until_success_within_the_attempt_budget()
    {
        var middleware = CreateMiddleware(maxAttempts: 5);
        var attempts = 0;

        await middleware.InvokeAsync(BuildContext(), _ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        });

        attempts.Should().Be(3);
    }

    [Fact]
    public async Task InvokeAsync_gives_up_and_propagates_after_max_attempts()
    {
        var middleware = CreateMiddleware(maxAttempts: 2);
        var attempts = 0;

        var act = () => middleware.InvokeAsync(BuildContext(), _ =>
        {
            attempts++;
            throw new InvalidOperationException("persistent");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("persistent");
        attempts.Should().Be(2);
    }
}
