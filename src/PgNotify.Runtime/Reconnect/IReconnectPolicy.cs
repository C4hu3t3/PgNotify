namespace PgNotify.Reconnect;

/// <summary>
/// Decides how long to wait before the next reconnect attempt after the notification listener's
/// connection is lost.
/// </summary>
public interface IReconnectPolicy
{
    /// <summary>
    /// Returns the delay before reconnect attempt number <paramref name="attempt"/> (1-based), or
    /// <see langword="null"/> to give up and let the failure propagate.
    /// </summary>
    TimeSpan? GetDelay(int attempt);
}
