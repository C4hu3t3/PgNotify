using Microsoft.CodeAnalysis;

namespace PgNotify.Analyzers;

/// <summary>The diagnostic descriptors for all <c>PGN0xx</c> rules.</summary>
internal static class NotificationDiagnostics
{
    private const string Category = "Notifications";

    public static readonly DiagnosticDescriptor DuplicateConfiguration = new(
        id: "PGN001",
        title: "Entity is configured for notifications both by attribute and fluent API",
        messageFormat: "'{0}' has both [NotifyChanges] and a HasDatabaseNotifications() call; the fluent call silently overrides the attribute — remove one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "When both configuration styles are used for the same entity, the explicit fluent API call always wins, which usually indicates the attribute was left behind by mistake.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor UnsupportedWatchedProperty = new(
        id: "PGN002",
        title: "OnUpdate() watches a navigation/collection property",
        messageFormat: "'{0}' is a navigation or collection property and cannot be watched for changes; only scalar mapped columns can be compared with IS DISTINCT FROM",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "OnUpdate(x => ...) selects columns for the generated trigger's change-detection guard, which only makes sense for scalar properties mapped to a single column.");

    public static readonly DiagnosticDescriptor MissingRuntimeRegistration = new(
        id: "PGN003",
        title: "Notifications are configured but AddPostgresNotifications() was never called",
        messageFormat: "'{0}' is configured for database notifications, but no call to AddPostgresNotifications(...) was found in this compilation; triggers will fire but nothing will listen",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "HasDatabaseNotifications()/[NotifyChanges] only configures the database side (triggers). The PgNotify.Runtime listener must also be registered to actually consume the notifications.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor UnsupportedPayloadProperty = new(
        id: "PGN004",
        title: "WithPayload() projects a navigation/collection property",
        messageFormat: "'{0}' is a navigation or collection property and cannot be projected into the notification payload; the trigger reads projected values straight off NEW/OLD, so only scalar mapped columns work",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "WithPayload(x => ...) selects columns the generated trigger reads into json_build_object, which only makes sense for scalar properties mapped to a single column.");
}
