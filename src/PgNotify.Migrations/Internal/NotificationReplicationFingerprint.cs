using System.Security.Cryptography;
using System.Text;
using PgNotify.Model;

namespace PgNotify.Migrations.Internal;

/// <summary>
/// The <see cref="NotificationDeliveryMode.LogicalReplication"/> counterpart to
/// <see cref="NotificationFingerprint"/>: a diff signal for
/// <see cref="Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsModelDiffer"/>, derived
/// from the SQL <see cref="NotificationReplicationSqlBuilder"/> would generate rather than from a
/// description of the configuration, for the same reason — anything the fingerprint does not cover
/// is never regenerated, so describing only the configuration would leave the same kind of gap a
/// fix to the SQL builder itself falling through for users whose configuration never changed.
/// </summary>
/// <remarks>
/// A separate annotation from <see cref="NotificationFingerprint"/>, not a shared one, because the
/// two are mutually exclusive per table: an entity is on exactly one delivery mode, and
/// <see cref="NpgsqlNotificationsAnnotationProvider"/>/<see cref="NpgsqlNotificationsMigrationsAnnotationProvider"/>
/// attach only the fingerprint matching that mode. Keeping them distinct lets
/// <see cref="NpgsqlNotificationsMigrationsSqlGenerator"/> tell "this table switched from Notify to
/// LogicalReplication" (trigger fingerprint present on the old side, replication fingerprint
/// present on the new side, both need acting on) from "nothing changed" without decoding either one.
/// </remarks>
internal static class NotificationReplicationFingerprint
{
    public const string AnnotationName = "Notifications:ReplicationFingerprint";

    /// <summary>
    /// Mirrors <see cref="NotificationFingerprint.NamePrefixAnnotationName"/>: removal-time SQL
    /// generation runs after the current configuration can no longer answer "what prefix (and
    /// therefore what publication name) was in effect?", so it must be read directly off the
    /// operation instead.
    /// </summary>
    public const string NamePrefixAnnotationName = "Notifications:ReplicationNamePrefix";

    public static string Compute(NotificationEntityConfiguration config, IReadOnlyList<string> generatedStatements)
    {
        var builder = new StringBuilder();

        builder.Append("ops=").Append(config.Operations);
        builder.Append(";watched=").Append(string.Join(",", config.WatchedUpdateColumns));
        builder.Append(";prefix=").Append(config.NamePrefix);
        builder.Append(";replicaIdentityFull=").Append(config.ReplicaIdentityFull);
        builder.Append(";consumerGroup=").Append(config.ReplicationConsumerGroup);
        builder.Append(";sql=").Append(HashStatements(generatedStatements));

        return builder.ToString();
    }

    private static string HashStatements(IReadOnlyList<string> generatedStatements) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(StripLineEndings(string.Join("\n", generatedStatements)))));

    // Same rationale as NotificationFingerprint.StripLineEndings: line endings vary by environment
    // (Environment.NewLine in StringBuilder.AppendLine, and the checkout's own line endings in raw
    // string literals), so removing them is required for a stable cross-environment fingerprint,
    // not cosmetic. See NotificationFingerprint for the full explanation.
    private static string StripLineEndings(string sql) => sql.Replace("\r", "").Replace("\n", "");
}
