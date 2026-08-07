using PgNotify.Serialization;

namespace PgNotify;

/// <summary>
/// Handles every notification received, whatever entity or operation it reports — including ones
/// arriving on a channel mapped without an entity type at all
/// (<c>PostgresNotificationsOptions.MapChannel("audit_events")</c>), which is what a listener with
/// no CLR entity types to key on uses.
/// </summary>
/// <remarks>
/// Runs after the entity-keyed handlers for the same notification (see
/// <see cref="IDatabaseNotificationHandler{TEntity}"/>). A class implementing both this interface
/// and an entity-keyed one is therefore invoked twice per matching notification: they are two
/// registrations, and both are honored.
/// </remarks>
public interface IDatabaseNotificationHandler
{
    /// <summary>Handles one received notification.</summary>
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Handles every notification for <typeparamref name="TEntity"/>, whatever the operation. For a
/// single operation, implement <see cref="IDatabaseInsertedHandler{TEntity}"/>,
/// <see cref="IDatabaseUpdatedHandler{TEntity}"/> or <see cref="IDatabaseDeletedHandler{TEntity}"/>
/// instead — the container then knows who listens to what, and a notification whose operation
/// nobody handles resolves nothing at all.
/// </summary>
/// <typeparam name="TEntity">
/// The mapped entity type, not an event type: it is the routing key, and never appears in
/// <see cref="HandleAsync"/>'s signature. Which channel carries it is declared by
/// <c>PostgresNotificationsOptions.MapChannel&lt;TEntity&gt;(channel)</c>.
/// </typeparam>
/// <remarks>
/// The envelope carries the row's key(s), the operation, and the raw payload; a projected payload
/// is bound to a .NET shape with an explicit <c>envelope.ToTyped&lt;T&gt;()</c> in the handler.
/// Handlers are resolved from a DI scope created per notification, so they can safely depend on
/// scoped services such as a <c>DbContext</c>; multiple handlers for the same entity are all
/// invoked, in registration order, one after the other.
/// </remarks>
public interface IDatabaseNotificationHandler<TEntity>
{
    /// <summary>Handles one received notification for <typeparamref name="TEntity"/>.</summary>
    Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken);
}
