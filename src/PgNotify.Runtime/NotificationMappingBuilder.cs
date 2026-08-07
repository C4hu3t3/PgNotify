using PgNotify.Internal;

namespace PgNotify;

/// <summary>
/// What an <see cref="INotificationMappingSource"/> writes into while the listener starts: the
/// channels to <c>LISTEN</c> on, which entity type each carries, and — for a source that knows one,
/// such as an EF Core <c>DbContext</c> — a connection string to fall back on.
/// </summary>
public sealed class NotificationMappingBuilder
{
    private readonly NotificationChannelMap _map;

    internal NotificationMappingBuilder(NotificationChannelMap map) => _map = map;

    private readonly HashSet<string> _proposedConnectionStrings = new(StringComparer.Ordinal);

    /// <summary>
    /// The distinct connection strings proposed by sources, used only when none was configured
    /// explicitly — an explicit <see cref="PostgresNotificationsOptions.ConnectionString"/> always
    /// wins. More than one means the sources disagree, which the caller has to settle: picking one
    /// would send the listener to a database nobody chose.
    /// </summary>
    public IReadOnlyCollection<string> ProposedConnectionStrings => _proposedConnectionStrings;

    /// <summary>Listens on <paramref name="channel"/> without binding it to an entity type.</summary>
    /// <exception cref="ArgumentException"><paramref name="channel"/> is null or whitespace.</exception>
    public NotificationMappingBuilder MapChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        _map.MapChannel(channel);
        return this;
    }

    /// <summary>
    /// Listens on <paramref name="channel"/> and routes notifications reporting
    /// <paramref name="entityName"/> to <paramref name="entityType"/>'s handlers.
    /// </summary>
    /// <param name="channel">The channel to <c>LISTEN</c> on.</param>
    /// <param name="entityName">
    /// The name the payload's <c>"entity"</c> field carries, which is the model's own display name
    /// for the entity — not necessarily <c>entityType.Name</c>, which is all a source working from
    /// CLR types alone could supply.
    /// </param>
    /// <param name="entityType">The CLR type whose handlers receive those notifications.</param>
    /// <exception cref="ArgumentException"><paramref name="channel"/> or <paramref name="entityName"/> is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Another type is already mapped to the same channel under the same entity name.
    /// </exception>
    public NotificationMappingBuilder MapChannel(string channel, string entityName, Type entityType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(entityType);

        _map.MapChannel(channel, entityName, entityType);
        return this;
    }

    /// <summary>
    /// Proposes <paramref name="connectionString"/> as the listener's connection string, for use
    /// when none was configured explicitly. A source is responsible for handing over a string a
    /// <c>LISTEN</c> connection can actually use — see
    /// <see cref="NotificationConnectionString.ForListening"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or whitespace.</exception>
    public NotificationMappingBuilder UseConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _proposedConnectionStrings.Add(connectionString);
        return this;
    }
}
