namespace PgNotify.Runtime.Tests;

/// <summary>
/// What <see cref="NotificationConnectionString.ForListening"/> appends, which depends on the Npgsql
/// major: Npgsql 11 (net11.0) no longer has a <c>Multiplexing</c> setting to turn off.
/// </summary>
internal static class ListeningConnectionString
{
#if NET11_0_OR_GREATER
    public const string Suffix = ";Pooling=False";
#else
    public const string Suffix = ";Multiplexing=False;Pooling=False";
#endif
}
