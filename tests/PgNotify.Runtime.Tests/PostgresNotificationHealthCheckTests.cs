using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using PgNotify.Internal;
using PgNotify.Reconnect;

namespace PgNotify.Runtime.Tests;

public class PostgresNotificationHealthCheckTests
{
    private static NpgsqlNotificationListener NeverConnectedListener() => new(
        new NotificationRuntimeState(),
        new ExponentialBackoffReconnectPolicy(),
        NullLogger<NpgsqlNotificationListener>.Instance);

    [Fact]
    public async Task Reports_unhealthy_when_the_listener_has_never_connected()
    {
        var healthCheck = new PostgresNotificationHealthCheck(NeverConnectedListener());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Data["connectedAt"].Should().Be("never");
        result.Data["lastNotificationAt"].Should().Be("never");
    }
}
