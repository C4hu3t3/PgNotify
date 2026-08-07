using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PgNotify.Analyzers;

/// <summary>
/// Catches three common PostgreSQL notification configuration mistakes at compile time: PGN001
/// (configured both by attribute and fluent API), PGN002 (watching a navigation/collection
/// property), PGN003 (configured but the runtime listener is never registered).
/// </summary>
/// <remarks>
/// Scoped down deliberately: PGN002's "is this a navigation" check is a type-shape heuristic
/// (collection or non-primitive reference type), not real EF Core model knowledge, so it can
/// have false positives for scalar reference types (e.g. a value-converted column). PGN003 checks
/// whether <c>AddPostgresNotifications(...)</c> appears anywhere in the compilation, not whether
/// it's actually reachable from an entry point — full reachability analysis is out of scope for a
/// lightweight analyzer.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NotificationConfigurationAnalyzer : DiagnosticAnalyzer
{
    private const string NotifyChangesAttributeMetadataName = "PgNotify.NotifyChangesAttribute";
    private const string HasDatabaseNotificationsMethodName = "HasDatabaseNotifications";
    private const string OnUpdateMethodName = "OnUpdate";
    private const string WithPayloadMethodName = "WithPayload";
    private const string AddPostgresNotificationsMethodName = "AddPostgresNotifications";
    private const string NotificationOptionsBuilderTypeName = "NotificationOptionsBuilder";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        NotificationDiagnostics.DuplicateConfiguration,
        NotificationDiagnostics.UnsupportedWatchedProperty,
        NotificationDiagnostics.MissingRuntimeRegistration,
        NotificationDiagnostics.UnsupportedPayloadProperty,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilationContext =>
        {
            var state = new AnalysisState();

            compilationContext.RegisterSyntaxNodeAction(ctx => AnalyzeClassDeclaration(ctx, state), SyntaxKind.ClassDeclaration);
            compilationContext.RegisterSyntaxNodeAction(ctx => AnalyzeInvocation(ctx, state), SyntaxKind.InvocationExpression);
            compilationContext.RegisterCompilationEndAction(endContext => ReportCrossCuttingDiagnostics(endContext, state));
        });
    }

    private static void AnalyzeClassDeclaration(SyntaxNodeAnalysisContext context, AnalysisState state)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration, context.CancellationToken);
        if (symbol is null)
        {
            return;
        }

        var hasNotifyChangesAttribute = symbol.GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString() == NotifyChangesAttributeMetadataName);

        if (hasNotifyChangesAttribute)
        {
            lock (state.SyncRoot)
            {
                state.AttributeConfiguredTypes[symbol] = classDeclaration.Identifier.GetLocation();
                state.AllConfiguredTypes[symbol] = classDeclaration.Identifier.GetLocation();
            }
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, AnalysisState state)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        switch (methodSymbol.Name)
        {
            case HasDatabaseNotificationsMethodName when methodSymbol.TypeArguments.Length == 1:
                AnalyzeHasDatabaseNotifications(state, methodSymbol, invocation);
                break;

            case OnUpdateMethodName when methodSymbol.ContainingType?.Name == NotificationOptionsBuilderTypeName:
                AnalyzeSelectedProperties(context, invocation, NotificationDiagnostics.UnsupportedWatchedProperty);
                break;

            // WithPayload has several overloads; only the projecting one takes a lambda, and the
            // others carry no member accesses for AnalyzeSelectedProperties to find anyway.
            case WithPayloadMethodName when methodSymbol.ContainingType?.Name == NotificationOptionsBuilderTypeName:
                AnalyzeSelectedProperties(context, invocation, NotificationDiagnostics.UnsupportedPayloadProperty);
                break;

            case AddPostgresNotificationsMethodName:
                state.HasRuntimeRegistration = true;
                break;
        }
    }

    private static void AnalyzeHasDatabaseNotifications(
        AnalysisState state,
        IMethodSymbol methodSymbol,
        InvocationExpressionSyntax invocation)
    {
        if (methodSymbol.TypeArguments[0] is not INamedTypeSymbol entityType)
        {
            return;
        }

        var location = invocation.GetLocation();

        // PGN001 needs both the [NotifyChanges] class declarations and every fluent call site
        // fully collected before it can safely cross-reference them — with concurrent execution
        // enabled, syntax nodes are not visited in source order, so the check itself happens in
        // ReportCrossCuttingDiagnostics (compilation end), not eagerly here.
        lock (state.SyncRoot)
        {
            state.FluentConfiguredTypes[entityType] = location;
            state.AllConfiguredTypes[entityType] = location;
        }
    }

    private static void AnalyzeSelectedProperties(
        SyntaxNodeAnalysisContext context,
        InvocationExpressionSyntax invocation,
        DiagnosticDescriptor descriptor)
    {
        var lambdaArgument = invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<LambdaExpressionSyntax>()
            .FirstOrDefault();

        if (lambdaArgument is not { ExpressionBody: { } body })
        {
            return;
        }

        foreach (var memberAccess in GetSelectedMembers(body))
        {
            if (context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken).Symbol is not IPropertySymbol propertySymbol || IsLikelyScalarType(propertySymbol.Type))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(descriptor, memberAccess.GetLocation(), propertySymbol.Name));
        }
    }

    private static IEnumerable<MemberAccessExpressionSyntax> GetSelectedMembers(ExpressionSyntax body) => body switch
    {
        AnonymousObjectCreationExpressionSyntax anonymous => anonymous.Initializers
            .Select(i => i.Expression)
            .OfType<MemberAccessExpressionSyntax>(),
        MemberAccessExpressionSyntax single => [single],
        _ => [],
    };

    private static bool IsLikelyScalarType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            type = nullable.TypeArguments[0];
        }

        return type.TypeKind == TypeKind.Enum || type.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single
                or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_Char or SpecialType.System_String or SpecialType.System_DateTime
                    => true,
            _ => type.ToDisplayString() is "System.Guid" or "System.DateTimeOffset" or "System.TimeSpan" or "System.DateOnly" or "System.TimeOnly",
        };
    }

    private static void ReportCrossCuttingDiagnostics(CompilationAnalysisContext context, AnalysisState state)
    {
        foreach (var entry in state.FluentConfiguredTypes.Where(entry => state.AttributeConfiguredTypes.ContainsKey(entry.Key)))
        {
            context.ReportDiagnostic(Diagnostic.Create(NotificationDiagnostics.DuplicateConfiguration, entry.Value, entry.Key.Name));
        }

        if (state.HasRuntimeRegistration || state.AllConfiguredTypes.Count == 0)
        {
            return;
        }

        foreach (var entry in state.AllConfiguredTypes)
        {
            context.ReportDiagnostic(Diagnostic.Create(NotificationDiagnostics.MissingRuntimeRegistration, entry.Value, entry.Key.Name));
        }
    }

    private sealed class AnalysisState
    {
        public readonly object SyncRoot = new();
        public readonly Dictionary<ISymbol, Location> AttributeConfiguredTypes = new(SymbolEqualityComparer.Default);
        public readonly Dictionary<ISymbol, Location> FluentConfiguredTypes = new(SymbolEqualityComparer.Default);
        public readonly Dictionary<ISymbol, Location> AllConfiguredTypes = new(SymbolEqualityComparer.Default);
        public volatile bool HasRuntimeRegistration;
    }
}
