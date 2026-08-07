using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using Microsoft.Extensions.DependencyInjection;
using PgNotify.Analyzers;

namespace PgNotify.Analyzers.Tests;

/// <summary>
/// Builds a minimal, real C# compilation (referencing PgNotify.Core/EFCore/Runtime plus the
/// BCL via the current runtime's trusted platform assemblies) and runs
/// <see cref="NotificationConfigurationAnalyzer"/> against it directly via
/// <see cref="CompilationWithAnalyzers"/> — no Microsoft.CodeAnalysis.Testing harness.
/// </summary>
internal static class CompilationTestHelper
{
    private static readonly Lazy<MetadataReference[]> References = new(BuildReferences);

    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: "PgNotify.Analyzers.Tests.Compilation",
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: References.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (compilationErrors.Length > 0)
        {
            throw new InvalidOperationException(
                "Test source failed to compile:\n" + string.Join("\n", compilationErrors.Select(d => d.ToString())));
        }

        var withAnalyzers = compilation.WithAnalyzers([new NotificationConfigurationAnalyzer()]);
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics;
    }

    private static MetadataReference[] BuildReferences()
    {
        var trustedPlatformAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        var references = trustedPlatformAssemblies.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        Type[] anchorTypes =
        [
            typeof(NotifyChangesAttribute), // PgNotify.Core
            typeof(EntityTypeBuilderNotificationExtensions), // PgNotify.EFCore
            typeof(ServiceCollectionExtensions), // PgNotify.Runtime
            typeof(DbContext), // Microsoft.EntityFrameworkCore
        ];

        var anchorReferences = anchorTypes.Select(t => (MetadataReference)MetadataReference.CreateFromFile(t.Assembly.Location));

        return [.. references, .. anchorReferences];
    }
}
