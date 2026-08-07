using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.TestModels;

/// <summary>
/// Stands in for a mapped entity CLR type: handlers and <c>IEntityChangeTracker&lt;TEntity&gt;</c>
/// are both keyed on it, never on an event type.
/// </summary>
public sealed class TestUser
{
    public int Id { get; set; }
}

/// <summary>A second mapped entity, so "handlers for the wrong entity stay untouched" is testable.</summary>
public sealed class TestOrder
{
    public int Id { get; set; }
}

/// <summary>
/// Records what it received, and under which handler role, so tests can assert both that a handler
/// ran and in which order relative to the other groups. The <paramref name="log"/> parameter is
/// optional because <c>AddHandlersFromAssembly</c> scans of this assembly activate these types
/// through plain DI, with nothing to supply.
/// </summary>
public sealed class RecordingUserUpdatedHandler(List<string>? log = null) : IDatabaseUpdatedHandler<TestUser>
{
    public List<NotificationEnvelope> Received { get; } = [];

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Add(envelope);
        log?.Add("updated");
        return Task.CompletedTask;
    }
}

public sealed class SecondUserUpdatedHandler : IDatabaseUpdatedHandler<TestUser>
{
    public static int InvocationCount;

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InvocationCount);
        return Task.CompletedTask;
    }
}

public sealed class DelegateUserUpdatedHandler(Func<NotificationEnvelope, Task>? handle = null) : IDatabaseUpdatedHandler<TestUser>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken) =>
        (handle ?? (_ => Task.CompletedTask))(envelope);
}

public sealed class RecordingUserInsertedHandler : IDatabaseInsertedHandler<TestUser>
{
    public List<NotificationEnvelope> Received { get; } = [];

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Add(envelope);
        return Task.CompletedTask;
    }
}

public sealed class RecordingUserDeletedHandler : IDatabaseDeletedHandler<TestUser>
{
    public List<NotificationEnvelope> Received { get; } = [];

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Add(envelope);
        return Task.CompletedTask;
    }
}

public sealed class RecordingUserHandler(List<string>? log = null) : IDatabaseNotificationHandler<TestUser>
{
    public List<NotificationEnvelope> Received { get; } = [];

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Add(envelope);
        log?.Add("entity");
        return Task.CompletedTask;
    }
}

public sealed class RecordingCatchAllHandler(List<string>? log = null) : IDatabaseNotificationHandler
{
    public List<NotificationEnvelope> Received { get; } = [];

    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        Received.Add(envelope);
        log?.Add("catch-all");
        return Task.CompletedTask;
    }
}
