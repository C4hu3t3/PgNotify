using PgNotify.Serialization;

namespace PgNotify.Dispatch;

/// <summary>
/// The per-notification context passed through the middleware pipeline, analogous to
/// <c>HttpContext</c> in ASP.NET Core middleware.
/// </summary>
public sealed class NotificationContext
{
    /// <summary>The parsed notification.</summary>
    public required NotificationEnvelope Envelope { get; init; }

    /// <summary>
    /// A DI scope created for this notification, so handlers can depend on scoped services
    /// (e.g. a <c>DbContext</c>). Disposed automatically after the pipeline completes.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Signals shutdown of the notification listener.</summary>
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>The next step in the notification dispatch pipeline.</summary>
public delegate Task NotificationDelegate(NotificationContext context);
