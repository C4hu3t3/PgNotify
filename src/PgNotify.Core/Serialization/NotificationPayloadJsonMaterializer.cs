using System.Text.Json;
using PgNotify.Model;
using PgNotify.Payloads;

namespace PgNotify.Serialization;

/// <summary>
/// Builds the same JSON text <c>PgNotify.Migrations</c>' <c>NotificationTriggerSqlBuilder</c> would
/// have the trigger emit via <c>json_build_object(...)</c> — but from already-decoded column values
/// instead of a SQL expression. This is what lets <c>PgNotify.Runtime.Replication</c> hand its
/// output straight to the same <see cref="INotificationPayloadDeserializer"/> the NOTIFY path uses
/// (<see cref="DefaultNotificationPayloadDeserializer"/> or a user's own), so a
/// <see cref="NotificationDeliveryMode.LogicalReplication"/>-delivered notification's
/// <see cref="NotificationEnvelope"/> — and anything built from it, including
/// <c>envelope.ToTyped&lt;T&gt;()</c> — is structurally identical to what the same configuration
/// would have produced under <see cref="NotificationDeliveryMode.Notify"/>, rather than a
/// second, independently-shaped representation of the same information.
/// </summary>
public static class NotificationPayloadJsonMaterializer
{
    /// <param name="config">The resolved notification configuration to materialize a payload for.</param>
    /// <param name="operation">The operation that occurred.</param>
    /// <param name="currentValues">
    /// The row's current column values — <c>NEW</c> for insert/update, or whatever the replication
    /// stream carries for a deleted row's old values (always at least
    /// <see cref="NotificationEntityConfiguration.KeyColumns"/>, the full row only under
    /// <see cref="NotificationEntityConfiguration.ReplicaIdentityFull"/>) — keyed by column name.
    /// </param>
    /// <param name="previousValues">
    /// The row's values before an update, keyed by column name, or <see langword="null"/> when
    /// unavailable (always the case for insert/delete; for update, only present under
    /// <see cref="NotificationEntityConfiguration.ReplicaIdentityFull"/>). Used only to compute the
    /// extended payload's <see cref="NotificationPayloadFieldKind.Changed"/> field — without it,
    /// <c>Changed</c> comes back empty rather than guessed, the same way an unconditional trigger
    /// update reports nothing changed because no comparison happened.
    /// </param>
    /// <param name="timestamp">
    /// The value for a <see cref="NotificationPayloadFieldKind.Timestamp"/> field — pass the
    /// replication message's own commit time for parity with the trigger's <c>clock_timestamp()</c>
    /// (both report when the change was committed, not when this code happens to run).
    /// </param>
    public static string Materialize(
        NotificationEntityConfiguration config,
        NotificationOperation operation,
        IReadOnlyDictionary<string, object?> currentValues,
        IReadOnlyDictionary<string, object?>? previousValues,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(currentValues);

        var effectiveWatchedColumns = config.UnconditionalUpdate
            ? []
            : config.WatchedUpdateColumns.Count > 0 ? config.WatchedUpdateColumns : [.. currentValues.Keys];

        var payload = new Dictionary<string, object?>();
        foreach (var field in config.BuildPayloadFields())
        {
            payload[field.JsonKey] = field.Kind switch
            {
                NotificationPayloadFieldKind.Constant => field.ConstantValue,
                NotificationPayloadFieldKind.Operation => operation.ToPastTenseWord(),
                NotificationPayloadFieldKind.Schema => config.Schema,
                NotificationPayloadFieldKind.Table => config.TableName,
                NotificationPayloadFieldKind.Column => currentValues.GetValueOrDefault(field.ColumnName!),
                NotificationPayloadFieldKind.Keys => BuildKeys(config.KeyColumns, currentValues),
                NotificationPayloadFieldKind.Changed => operation == NotificationOperation.Update
                    ? BuildChanged(effectiveWatchedColumns, previousValues, currentValues)
                    : Array.Empty<string>(),
                NotificationPayloadFieldKind.Timestamp => timestamp,
                _ => null,
            };
        }

        return JsonSerializer.Serialize(payload);
    }

    private static Dictionary<string, object?> BuildKeys(IReadOnlyList<string> keyColumns, IReadOnlyDictionary<string, object?> values) =>
        keyColumns.ToDictionary(column => column, values.GetValueOrDefault);

    private static string[] BuildChanged(
        IReadOnlyList<string> watchedColumns,
        IReadOnlyDictionary<string, object?>? previousValues,
        IReadOnlyDictionary<string, object?> currentValues)
    {
        if (previousValues is null || watchedColumns.Count == 0)
        {
            return [];
        }

        return
        [
            .. watchedColumns.Where(column =>
                !Equals(previousValues.GetValueOrDefault(column), currentValues.GetValueOrDefault(column))),
        ];
    }
}
