using Npgsql;

namespace PgNotify;

/// <summary>
/// Turns a connection string written for an application's normal queries into one a dedicated
/// <c>LISTEN</c> connection can actually hold, which is not the same thing.
/// </summary>
public static class NotificationConnectionString
{
    /// <summary>
    /// Returns <paramref name="connectionString"/> with the two settings that are hostile to a
    /// <c>LISTEN</c> connection turned off.
    /// </summary>
    /// <remarks>
    /// Both were measured against <c>postgres:16-alpine</c> with Npgsql 10:
    /// <list type="bullet">
    /// <item>
    /// <c>Multiplexing=true</c> makes it impossible to wait. The failure is not at <c>LISTEN</c>,
    /// which succeeds — it is <c>NpgsqlConnection.WaitAsync</c> throwing
    /// <c>NotSupportedException: Wait isn't supported in multiplexing mode</c>, immediately, on
    /// every attempt. The listener degrades into a permanent reconnect loop: measured at 32
    /// reconnects in 7 seconds, each logging an error and opening a connection. Notifications do
    /// still get through — 10 of 10 in that same measurement, because each attempt's <c>LISTEN</c>
    /// round trip drains what the server has pending — so the symptom is a listener that works
    /// while hammering the database and filling the log, not one that goes quiet. A reconnect
    /// policy with a bounded attempt count turns it into a hard failure instead.
    /// </item>
    /// <item>
    /// <c>Pooling=true</c> is not an error, but a <c>LISTEN</c> registration is connection-scoped, so
    /// the listener holds its connection open for the process's lifetime — and pools are keyed by
    /// connection string, so it holds a slot of the application's own pool. Measured with
    /// <c>MaxPoolSize=1</c>: the application cannot open a connection at all until the listener is
    /// taken out of the pool.
    /// </item>
    /// </list>
    /// Everything else — host, credentials, TLS, timeouts, <c>ApplicationName</c> — is preserved.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or whitespace.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="connectionString"/> is not a valid Npgsql connection string.</exception>
    public static string ForListening(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Multiplexing = false,
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// Returns <paramref name="connectionString"/> adjusted for a dedicated logical replication
    /// streaming connection: the same <c>Multiplexing</c>/<c>Pooling</c> settings <see cref="ForListening"/>
    /// turns off, for the same reason — this connection is held open for the listener's lifetime,
    /// never shared or waited on the way pooled/multiplexed connections expect to be.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> add a <c>Replication=...</c> keyword to the connection string:
    /// confirmed against Npgsql 10 that <c>NpgsqlConnectionStringBuilder</c> does not recognize that
    /// keyword at all and throws attempting to parse one. <c>Npgsql.Replication.LogicalReplicationConnection</c>
    /// sets the replication mode itself once opened — this method's job is the same kind of
    /// correction <see cref="ForListening"/> already makes, not an additive one.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null or whitespace.</exception>
    public static string ForReplication(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Multiplexing = false,
            Pooling = false,
        }.ToString();
    }
}
