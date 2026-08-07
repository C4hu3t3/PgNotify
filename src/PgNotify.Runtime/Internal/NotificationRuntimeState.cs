namespace PgNotify.Internal;

/// <summary>
/// What the listener needs to connect, resolved once at <c>StartAsync</c> rather than when the
/// services were registered: the connection string and the full channel set.
/// </summary>
/// <remarks>
/// It exists because both can come from an <see cref="INotificationMappingSource"/> — a
/// <c>DbContext</c> registered after <c>AddPostgresNotifications()</c>, for instance — while
/// <see cref="NpgsqlNotificationListener"/> is a singleton the health check resolves long before
/// any of that is known. The listener reads this state when it connects, and on every reconnect.
/// </remarks>
internal sealed class NotificationRuntimeState
{
    public string ConnectionString { get; set; } = "";

    public IReadOnlyCollection<string> Channels { get; set; } = [];
}
