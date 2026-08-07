; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category      | Severity | Notes
--------|---------------|----------|----------------------------------------------------------------------
PGN001  | Notifications | Warning  | Entity is configured for notifications both by attribute and fluent API
PGN002  | Notifications | Error    | OnUpdate() watches a navigation/collection property
PGN003  | Notifications | Info     | Notifications are configured but AddPostgresNotifications() was never called
PGN004  | Notifications | Error    | WithPayload() projects a navigation/collection property
