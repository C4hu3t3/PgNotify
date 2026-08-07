using Microsoft.Extensions.Diagnostics.HealthChecks;
using PgNotify.Internal;

namespace PgNotify;

/// <summary>
/// Reports whether the notification listener's dedicated LISTEN connection is currently
/// connected, plus how long ago it last received a notification (useful to spot a "connected but
/// stuck" listener — connected does not by itself prove notifications are flowing).
/// </summary>
internal sealed class PostgresNotificationHealthCheck(NpgsqlNotificationListener listener) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["connectedAt"] = listener.LastConnectedAt?.ToString("O") ?? "never",
            ["lastNotificationAt"] = listener.LastNotificationAt?.ToString("O") ?? "never",
        };

        var result = listener.IsConnected
            ? HealthCheckResult.Healthy("PostgreSQL notification listener is connected.", data)
            : HealthCheckResult.Unhealthy("PostgreSQL notification listener is not connected.", data: data);

        return Task.FromResult(result);
    }
}
