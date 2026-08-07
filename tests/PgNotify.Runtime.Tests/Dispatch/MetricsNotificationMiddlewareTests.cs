using System.Diagnostics.Metrics;
using PgNotify.Dispatch;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Dispatch;

public class MetricsNotificationMiddlewareTests
{
    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static NotificationContext BuildContext(string channel) => new()
    {
        Envelope = new NotificationEnvelope
        {
            Channel = channel,
            Entity = "User",
            Operation = NotificationOperation.Update,
            Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
            RawPayload = "{}",
        },
        Services = new NoopServiceProvider(),
        CancellationToken = CancellationToken.None,
    };

    [Fact]
    public async Task InvokeAsync_records_a_received_measurement()
    {
        var receivedCounts = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MetricsNotificationMiddleware.MeterName && instrument.Name == "notifications.received")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => receivedCounts.Add(measurement));
        listener.Start();

        var middleware = new MetricsNotificationMiddleware();
        await middleware.InvokeAsync(BuildContext("metrics-test-channel"), _ => Task.CompletedTask);

        receivedCounts.Should().Contain(1);
    }

    [Fact]
    public async Task InvokeAsync_records_a_failed_measurement_and_rethrows()
    {
        var failedCounts = new List<long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MetricsNotificationMiddleware.MeterName && instrument.Name == "notifications.failed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => failedCounts.Add(measurement));
        listener.Start();

        var middleware = new MetricsNotificationMiddleware();
        var act = () => middleware.InvokeAsync(BuildContext("metrics-fail-channel"), _ => throw new InvalidOperationException());

        await act.Should().ThrowAsync<InvalidOperationException>();
        failedCounts.Should().Contain(1);
    }
}
