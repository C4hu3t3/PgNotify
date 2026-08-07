using PgNotify.Serialization;

namespace PgNotify.Internal;

/// <summary>
/// The non-generic face of <see cref="EntityNotificationDispatcher{TEntity}"/>, so
/// <see cref="NotificationChannelMap"/> can hold dispatchers for many entity types in one
/// dictionary. The generic-to-non-generic bridge is built once, when the channel is mapped — the
/// per-notification path is a dictionary lookup and a virtual call, with no reflection.
/// </summary>
internal interface IEntityNotificationDispatcher
{
    Type EntityType { get; }

    Task DispatchAsync(NotificationEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken);
}
