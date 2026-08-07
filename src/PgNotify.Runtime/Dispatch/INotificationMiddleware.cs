namespace PgNotify.Dispatch;

/// <summary>
/// A pipeline stage that runs for every received notification, before it is dispatched to typed
/// handlers/<c>Events&lt;T&gt;()</c> subscribers. Composed like ASP.NET Core middleware: call
/// <c>next</c> to continue the pipeline, or return without calling it to short-circuit.
/// </summary>
public interface INotificationMiddleware
{
    /// <summary>Processes <paramref name="context"/>, calling <paramref name="next"/> to continue the pipeline.</summary>
    Task InvokeAsync(NotificationContext context, NotificationDelegate next);
}
