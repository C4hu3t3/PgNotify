using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PgNotify.Diagnostics;

/// <summary>
/// Logs notification-configuration warnings through EF Core's own diagnostics pipeline
/// (<see cref="EventDefinition{TParam}.Log{TLoggerCategory}"/>), the same mechanism EF Core's own
/// built-in warnings (shadow properties, conflicting foreign keys, ...) use, rather than a raw
/// <see cref="ILogger"/> call directly on <c>IDiagnosticsLogger{TLoggerCategory}.Logger</c> -
/// only events logged this way are eligible for a consumer's <c>ConfigureWarnings(...)</c>
/// (to promote/suppress/throw on this specific event) the same way any other EF Core warning is.
/// </summary>
internal static class NotificationLoggerExtensions
{
    private static readonly EventId ConflictingCompareColumnsAndWatchedPropertiesEventId =
        new(90100, "PgNotify.ConflictingCompareColumnsAndWatchedProperties");

    /// <summary>
    /// Logged when <c>[NotifyChanges(CompareColumnsOnUpdate = false)]</c> also sets
    /// <c>WatchedProperties</c>: the two are contradictory (nothing is compared either way), so
    /// <c>WatchedProperties</c> is silently ignored rather than the model build failing - but a
    /// leftover, never-applied <c>WatchedProperties</c> value is worth flagging.
    /// </summary>
    public static void ConflictingCompareColumnsAndWatchedPropertiesWarning(
        this IDiagnosticsLogger<DbLoggerCategory.Model.Validation> diagnostics,
        string entityTypeName)
    {
        var definition = new EventDefinition<string>(
            diagnostics.Options,
            ConflictingCompareColumnsAndWatchedPropertiesEventId,
            LogLevel.Warning,
            "PgNotify.ConflictingCompareColumnsAndWatchedProperties",
            level => LoggerMessage.Define<string>(
                level,
                ConflictingCompareColumnsAndWatchedPropertiesEventId,
                "[NotifyChanges] on '{EntityType}' sets both CompareColumnsOnUpdate = false and WatchedProperties. "
                + "WatchedProperties is ignored: an unconditional update watches no columns."));

        definition.Log(diagnostics, entityTypeName);
    }
}
