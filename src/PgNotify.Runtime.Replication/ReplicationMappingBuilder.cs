using System.Data.Common;
using PgNotify.Model;

namespace PgNotify;

/// <summary>
/// What an <see cref="IReplicationMappingSource"/> writes into while the replication listener
/// starts: which entities to stream, and — for a source that knows one, such as an EF Core
/// <c>DbContext</c> — a connection string to fall back on. The replication counterpart to
/// <see cref="NotificationMappingBuilder"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="NotificationMappingBuilder.UseConnection"/>, there is no credential-carrying
/// template mechanism here: <c>Npgsql.Replication.LogicalReplicationConnection</c> is constructed
/// from a plain connection string only, with no equivalent of <c>NpgsqlConnection.CloneWith</c> to
/// clone a data-source-bound connection's real credentials into. A <c>DbContext</c> configured via
/// <c>UseNpgsql(NpgsqlDataSource, ...)</c> reports a connection string with its password stripped —
/// for that case, set <see cref="PostgresLogicalReplicationOptions.ConnectionString"/> explicitly
/// with real credentials rather than relying on a source derived from the context's connection.
/// </remarks>
public sealed class ReplicationMappingBuilder
{
    private readonly List<ReplicationEntityMapping> _mappings = [];
    private readonly HashSet<string> _proposedConnectionStrings = new(StringComparer.Ordinal);

    /// <summary>Every entity contributed so far.</summary>
    public IReadOnlyList<ReplicationEntityMapping> Mappings => _mappings;

    /// <summary>
    /// The distinct connection strings proposed by sources, used only when none was configured
    /// explicitly — the same "more than one is an error, not a coin flip" rule
    /// <see cref="NotificationMappingBuilder.ProposedConnectionStrings"/> applies.
    /// </summary>
    public IReadOnlyCollection<string> ProposedConnectionStrings => _proposedConnectionStrings;

    /// <summary>Adds one <see cref="NotificationDeliveryMode.LogicalReplication"/>-configured entity.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> or <paramref name="entityType"/> is null.</exception>
    public ReplicationMappingBuilder AddEntity(NotificationEntityConfiguration config, Type entityType)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(entityType);

        _mappings.Add(new ReplicationEntityMapping(config, entityType));
        return this;
    }

    /// <summary>
    /// Proposes <paramref name="connectionString"/> as the replication listener's connection
    /// string, for use when none was configured explicitly.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or whitespace.</exception>
    public ReplicationMappingBuilder UseConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _proposedConnectionStrings.Add(connectionString);
        return this;
    }

    /// <summary>
    /// Proposes the replication connection string derived from <paramref name="connection"/>,
    /// adjusted via <see cref="NotificationConnectionString.ForReplication"/> — the replication
    /// counterpart to <see cref="NotificationMappingBuilder.UseConnection"/>, minus the credential
    /// -carrying template handling that method has (see the type-level remarks for why).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is null.</exception>
    public ReplicationMappingBuilder UseConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.ConnectionString is { Length: > 0 } raw)
        {
            UseConnectionString(NotificationConnectionString.ForReplication(raw));
        }

        return this;
    }
}
