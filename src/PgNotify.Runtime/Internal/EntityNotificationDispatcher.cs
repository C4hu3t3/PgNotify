using Microsoft.Extensions.DependencyInjection;
using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// Invokes the handlers registered for <typeparamref name="TEntity"/> from the current DI scope:
/// first the group matching the envelope's operation
/// (<see cref="IDatabaseInsertedHandler{TEntity}"/>/<see cref="IDatabaseUpdatedHandler{TEntity}"/>/
/// <see cref="IDatabaseDeletedHandler{TEntity}"/>), then
/// <see cref="IDatabaseNotificationHandler{TEntity}"/>, each in registration order.
/// </summary>
/// <remarks>
/// Only one operation group is ever resolved, which is the reason the operations are separate
/// interfaces rather than three default-implemented methods on one: a notification whose operation
/// nobody handles resolves nothing and invokes nothing, instead of resolving every handler for the
/// entity so that two no-ops out of three can be called.
/// </remarks>
internal sealed class EntityNotificationDispatcher<TEntity> : IEntityNotificationDispatcher
{
    public Type EntityType => typeof(TEntity);

    public async Task DispatchAsync(NotificationEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        switch (envelope.Operation)
        {
            case NotificationOperation.Insert:
                foreach (var handler in scopedServices.GetServices<IDatabaseInsertedHandler<TEntity>>())
                {
                    await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                }

                break;

            case NotificationOperation.Update:
                foreach (var handler in scopedServices.GetServices<IDatabaseUpdatedHandler<TEntity>>())
                {
                    await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                }

                break;

            case NotificationOperation.Delete:
                foreach (var handler in scopedServices.GetServices<IDatabaseDeletedHandler<TEntity>>())
                {
                    await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(envelope), envelope.Operation, "Unrecognized notification operation.");
        }

        foreach (var handler in scopedServices.GetServices<IDatabaseNotificationHandler<TEntity>>())
        {
            await handler.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }
}
