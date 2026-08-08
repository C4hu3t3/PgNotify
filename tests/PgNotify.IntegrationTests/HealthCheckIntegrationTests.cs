using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

/// <summary>
/// PostgresNotificationHealthCheck is internal, and InternalsVisibleTo from PgNotify.Runtime
/// doesn't reach this project - so unlike PostgresNotificationHealthCheckTests (which covers the
/// never-connected/unhealthy path without a database), the healthy path is exercised here through
/// the same public surface an application actually uses: AddHealthChecks() registers it under the
/// "postgres-notifications" name, resolvable via the public HealthCheckService.
/// </summary>
[Collection(nameof(NotificationHostCollection))]
public class HealthCheckIntegrationTests(NotificationHostFixture fixture)
{
    [Fact]
    public async Task Reports_healthy_with_a_notification_timestamp_once_a_change_has_flowed_through()
    {
        await using var context = new IntegrationDbContext(
            new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(fixture.Host.ConnectionString).Options);
        var user = new TestUser { Name = "Rosalind", Email = "rosalind@example.com" };
        var waitTask = NotificationWaiter.WaitAsync<TestUser>(fixture.Host.Notifications, NotificationOperation.Insert);

        context.Users.Add(user);
        await context.SaveChangesAsync();
        await waitTask;

        using var scope = fixture.Host.CreateScope();
        var healthCheckService = scope.ServiceProvider.GetRequiredService<HealthCheckService>();
        var report = await healthCheckService.CheckHealthAsync();

        var entry = report.Entries["postgres-notifications"];
        entry.Status.Should().Be(HealthStatus.Healthy);
        entry.Data["connectedAt"].Should().NotBe("never");
        entry.Data["lastNotificationAt"].Should().NotBe("never");
    }
}
