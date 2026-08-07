using PgNotify.Serialization;

namespace PgNotify.Benchmarks;

/// <summary>
/// Stands in for a mapped entity type: routing keys on it, so its name must match the
/// <c>"entity"</c> field of the payloads these benchmarks dispatch.
/// </summary>
public sealed class User
{
    public int Id { get; set; }
}

/// <summary>Fills the routing map alongside <see cref="User"/>, so lookups are not measured against a one-entry dictionary.</summary>
public sealed class Order
{
    public int Id { get; set; }
}

/// <inheritdoc cref="Order"/>
public sealed class Invoice
{
    public int Id { get; set; }
}

/// <inheritdoc cref="Order"/>
public sealed class Product
{
    public int Id { get; set; }
}

public sealed class NoOpUserUpdatedHandler : IDatabaseUpdatedHandler<User>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SecondNoOpUserUpdatedHandler : IDatabaseUpdatedHandler<User>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NoOpUserHandler : IDatabaseNotificationHandler<User>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class NoOpCatchAllHandler : IDatabaseNotificationHandler
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The hand-written shape <c>ToTyped&lt;T&gt;()</c> binds a minimal payload into.</summary>
public sealed record UserUpdatedShape(int Id, string Name);
