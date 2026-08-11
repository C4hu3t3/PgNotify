using PgNotify.Payloads;

namespace PgNotify.Metadata;

/// <summary>
/// Serializes a resolved notification configuration into the primitive annotation values defined
/// by <see cref="NotificationAnnotationNames"/>. Called by both the fluent
/// <c>HasDatabaseNotifications()</c> API and the <c>[NotifyChanges]</c> attribute convention, so
/// the two configuration styles always converge on identical annotation state.
/// </summary>
internal static class NotificationConfigurationWriter
{
    public static void Apply(
        Action<string, object?> setAnnotation,
        NotificationOperations operations,
        IReadOnlyList<string> watchedUpdateProperties,
        string channelStrategyKind,
        string? channelStrategyArgument,
        string? channelNameOverride,
        string payloadBuilderKind,
        string? payloadBuilderTypeName,
        string? namePrefix = null,
        NotificationPayloadOverflow payloadOverflow = NotificationPayloadOverflow.Truncate,
        IReadOnlyList<string>? payloadProperties = null,
        bool unconditionalUpdate = false)
    {
        // No operations selected means every write to HasDatabaseNotifications()/[NotifyChanges]
        // came from a bare call - defaulting that to All keeps both configuration styles agreeing:
        // the attribute already defaults a bare [NotifyChanges] to All at the constructor level, so
        // an explicit NotificationOperations.None on either style now converges here instead of one
        // style (the fluent one, previously) coercing it and the other persisting it untouched.
        if (operations == NotificationOperations.None)
        {
            operations = NotificationOperations.All;
        }

        setAnnotation(NotificationAnnotationNames.Enabled, true);
        setAnnotation(NotificationAnnotationNames.Operations, SerializeOperations(operations));
        setAnnotation(NotificationAnnotationNames.WatchedUpdateColumns, string.Join(",", watchedUpdateProperties));
        setAnnotation(NotificationAnnotationNames.UnconditionalUpdate, unconditionalUpdate);
        setAnnotation(NotificationAnnotationNames.ChannelStrategyKind, channelStrategyKind);
        setAnnotation(NotificationAnnotationNames.ChannelStrategyArgument, channelStrategyArgument);
        setAnnotation(NotificationAnnotationNames.ChannelNameOverride, channelNameOverride);
        setAnnotation(NotificationAnnotationNames.PayloadBuilderKind, payloadBuilderKind);
        setAnnotation(NotificationAnnotationNames.PayloadBuilderTypeName, payloadBuilderTypeName);
        setAnnotation(NotificationAnnotationNames.NamePrefix, namePrefix);
        setAnnotation(NotificationAnnotationNames.PayloadOverflow, payloadOverflow.ToString());
        setAnnotation(NotificationAnnotationNames.PayloadColumns, string.Join(",", payloadProperties ?? []));
    }

    public static string SerializeOperations(NotificationOperations operations) =>
        string.Join(",", operations.Expand());

    public static NotificationOperations ParseOperations(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return NotificationOperations.None;
        }

        var result = NotificationOperations.None;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<NotificationOperation>(part, out var operation))
            {
                result |= operation.ToFlag();
            }
        }

        return result;
    }
}
