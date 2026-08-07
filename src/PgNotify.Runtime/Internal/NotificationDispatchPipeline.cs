using Microsoft.Extensions.DependencyInjection;
using PgNotify.Dispatch;

namespace PgNotify.Internal;

/// <summary>
/// Composes the registered <see cref="INotificationMiddleware"/> stages around a terminal step that
/// delivers one notification to everything listening for it, built once at startup (mirrors how
/// ASP.NET Core composes its middleware pipeline once, not per request).
/// </summary>
/// <remarks>
/// The terminal step's order is fixed and tested rather than left to DI resolution order:
/// <c>Events&lt;TEntity&gt;()</c> subscribers first (they must not wait behind a slow handler), then
/// the entity's operation-specific handlers, then its
/// <see cref="IDatabaseNotificationHandler{TEntity}"/> handlers, then the non-generic
/// <see cref="IDatabaseNotificationHandler"/> catch-alls. Each group runs in registration order,
/// and each handler is awaited before the next one starts.
/// </remarks>
internal sealed class NotificationDispatchPipeline
{
    private readonly NotificationDelegate _pipeline;

    public NotificationDispatchPipeline(
        IEnumerable<INotificationMiddleware> middlewares,
        NotificationChannelMap channelMap,
        NotificationEventHub hub)
    {
        NotificationDelegate terminal = context => DispatchAsync(context, channelMap, hub);

        _pipeline = middlewares.Reverse().Aggregate(terminal, (next, middleware) => context => middleware.InvokeAsync(context, next));
    }

    public Task InvokeAsync(NotificationContext context) => _pipeline(context);

    private static async Task DispatchAsync(NotificationContext context, NotificationChannelMap channelMap, NotificationEventHub hub)
    {
        if (channelMap.Find(context.Envelope) is { } entityDispatcher)
        {
            hub.Publish(entityDispatcher.EntityType, context.Envelope);

            await entityDispatcher.DispatchAsync(context.Envelope, context.Services, context.CancellationToken).ConfigureAwait(false);
        }

        foreach (var handler in context.Services.GetServices<IDatabaseNotificationHandler>())
        {
            await handler.HandleAsync(context.Envelope, context.CancellationToken).ConfigureAwait(false);
        }
    }
}
