using System.Security.Cryptography;
using System.Text;
using PgNotify.Model;

namespace PgNotify.Migrations.Internal;

/// <summary>
/// Computes a deterministic string that changes whenever the trigger SQL this library would
/// generate changes. Stored as a single migrations annotation (<see cref="AnnotationName"/>)
/// purely as a diff signal for
/// <see cref="Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer"/> — the SQL
/// generator re-resolves the full, rich <see cref="NotificationEntityConfiguration"/> from the
/// model when it needs to actually build SQL, rather than trying to decode this string.
/// </summary>
/// <remarks>
/// Derived from the <b>generated SQL itself</b>, not from a description of the configuration,
/// because <c>NpgsqlNotificationsMigrationsSqlGenerator</c> skips regeneration entirely when the
/// fingerprint is unchanged: whatever the fingerprint does not cover is never regenerated.
/// Describing only the configuration left two holes. Adding a mapped column to an entity with an
/// unfiltered <c>OnUpdate()</c> produced no fingerprint change, so the deployed function kept its
/// old <c>IS DISTINCT FROM</c> guard and never notified for that column; and a fix to
/// <see cref="NotificationTriggerSqlBuilder"/> never reached users whose own configuration had not
/// changed. Hashing the produced SQL closes both by construction — including for changes made
/// inside this library, which a configuration summary can never see.
/// </remarks>
/// <remarks>
/// The relationship is deliberately one-way: different SQL always yields a different fingerprint
/// (the safety-critical direction), while the human-readable prefix means two configurations
/// producing byte-identical SQL may still differ here. That only ever costs a redundant
/// <c>CREATE OR REPLACE FUNCTION</c>, which is idempotent.
/// </remarks>
internal static class NotificationFingerprint
{
    public const string AnnotationName = "Notifications:Fingerprint";

    /// <summary>
    /// Surfaces <see cref="NotificationEntityConfiguration.NamePrefix"/> as its own migrations
    /// annotation (not folded into the opaque fingerprint), so removal-time SQL generation — which
    /// runs after the current configuration can no longer answer "what prefix was in effect?" —
    /// can read it directly off the operation instead. See
    /// <c>NpgsqlNotificationsMigrationsAnnotationProvider.ForRemove</c> and
    /// <c>NpgsqlNotificationsMigrationsSqlGenerator</c>'s removal handling.
    /// </summary>
    public const string NamePrefixAnnotationName = "Notifications:NamePrefix";

    /// <param name="config">The resolved notification configuration being fingerprinted.</param>
    /// <param name="generatedStatements">
    /// The statements <see cref="NotificationTriggerSqlBuilder.BuildUpsertStatements"/> produces
    /// for <paramref name="config"/>. This is what makes the fingerprint exhaustive; the readable
    /// prefix built from <paramref name="config"/> exists only to keep <c>ModelSnapshot.cs</c>
    /// diffs interpretable at a glance.
    /// </param>
    public static string Compute(NotificationEntityConfiguration config, IReadOnlyList<string> generatedStatements)
    {
        var builder = new StringBuilder();

        builder.Append("ops=").Append(config.Operations);
        builder.Append(";watched=").Append(string.Join(",", config.WatchedUpdateColumns));
        builder.Append(";prefix=").Append(config.NamePrefix);

        foreach (var operation in config.Operations.Expand())
        {
            builder.Append(';').Append(operation).Append("Channel=").Append(config.GetChannelName(operation));
        }

        builder.Append(";sql=").Append(HashStatements(generatedStatements));

        return builder.ToString();
    }

    /// <summary>
    /// A SHA-256 hex digest of <see cref="Compute"/>'s output, for
    /// <see cref="Microsoft.EntityFrameworkCore.DatabaseFacadeNotificationsExtensions"/> to store
    /// via <c>COMMENT ON FUNCTION</c> and compare on the next call: fixed length, no quoting
    /// concerns, at the cost of no longer being human-readable via <c>\df+</c> the way the raw
    /// fingerprint text is. The migrations path has no use for this - it never persists the
    /// fingerprint anywhere queryable, only as an opaque migrations annotation.
    /// </summary>
    public static string ComputeHash(NotificationEntityConfiguration config, IReadOnlyList<string> generatedStatements) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Compute(config, generatedStatements))));

    private static string HashStatements(IReadOnlyList<string> generatedStatements) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(StripLineEndings(string.Join("\n", generatedStatements)))));

    /// <summary>
    /// Removes line endings, and <b>only</b> line endings, before hashing.
    /// </summary>
    /// <remarks>
    /// Required for correctness rather than cosmetics: the generated text's line endings vary by
    /// environment, from two independent sources. <see cref="StringBuilder.AppendLine()"/> follows
    /// <see cref="Environment.NewLine"/> (so <c>\r\n</c> on Windows), and the raw string literals
    /// in <see cref="NotificationTriggerSqlBuilder"/> carry whatever line endings the <c>.cs</c>
    /// file itself was checked out with. Left alone, a Windows developer and a Linux CI fingerprint
    /// the same model differently, producing phantom migrations that ping-pong between teammates.
    /// <para>
    /// Removed outright rather than normalized to <c>\n</c> so the result does not depend on the
    /// repository's git configuration at all — a <c>.gitattributes</c> pinning <c>*.cs</c> to LF
    /// would fix the checkout half of the problem, but only for clones that actually have it.
    /// </para>
    /// <para>
    /// Runs of spaces and indentation are deliberately left alone. The whole function body sits
    /// inside a dollar-quoted literal, and channel names, JSON keys, and entity display names are
    /// single-quoted literals: collapsing whitespace would hash <c>WithChannelName("my channel")</c>
    /// and <c>WithChannelName("my  channel")</c> identically, reintroducing exactly the missed
    /// regeneration this type exists to prevent. The equivalent exposure for line endings is a
    /// channel or table name containing a literal newline, which nothing in the API sanitizes but
    /// which no real configuration produces.
    /// </para>
    /// </remarks>
    private static string StripLineEndings(string sql) => sql.Replace("\r", "").Replace("\n", "");
}
