using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DownKyi.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GlobalSqlitePoolCleanupAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "DKYI1001";
    public const string AllowedProcessOwnerAssembly = "DownKyi.SystemBenchmarks";
    public const string AllowedProcessOwnerPath =
        "benchmarks/DownKyi.SystemBenchmarks/Program.cs";
    public const string RepositoryRootMarkerMetadata =
        "build_metadata.AdditionalFiles.DownKyiRepositoryRootMarker";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Global SQLite pool cleanup requires process ownership",
        "Only the process-level SQLite owner may call SqliteConnection.ClearAllPools",
        "Lifecycle",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Component and test owners must clear only pools they own.",
        customTags: new[] { WellKnownDiagnosticTags.NotConfigurable });

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(compilationContext =>
        {
            var connectionType = compilationContext.Compilation.GetTypeByMetadataName(
                "Microsoft.Data.Sqlite.SqliteConnection");
            var clearAllPools = connectionType?
                .GetMembers("ClearAllPools")
                .OfType<IMethodSymbol>()
                .SingleOrDefault(method => method.IsStatic && method.Parameters.Length == 0);
            if (clearAllPools == null)
            {
                return;
            }

            compilationContext.RegisterOperationAction(operationContext =>
            {
                var targetMethod = operationContext.Operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IMethodReferenceOperation methodReference => methodReference.Method,
                    _ => null
                };
                if (SymbolEqualityComparer.Default.Equals(
                        targetMethod?.OriginalDefinition,
                        clearAllPools.OriginalDefinition) &&
                    !IsAllowedProcessOwner(
                        operationContext.Compilation,
                        operationContext.Operation.Syntax.SyntaxTree.FilePath,
                        operationContext.Options))
                {
                    operationContext.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        operationContext.Operation.Syntax.GetLocation()));
                }
            }, OperationKind.Invocation, OperationKind.MethodReference);
        });
    }

    private static bool IsAllowedProcessOwner(
        Compilation compilation,
        string sourcePath,
        AnalyzerOptions analyzerOptions)
    {
        if (!string.Equals(
                compilation.AssemblyName,
                AllowedProcessOwnerAssembly,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sourcePath))
        {
            return false;
        }

        try
        {
            var repositoryRoots = analyzerOptions.AdditionalFiles
                .Where(file =>
                    analyzerOptions.AnalyzerConfigOptionsProvider
                        .GetOptions(file)
                        .TryGetValue(RepositoryRootMarkerMetadata, out var marker) &&
                    string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetDirectoryName(Path.GetFullPath(file.Path)))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (repositoryRoots.Length != 1)
            {
                return false;
            }

            var repositoryRoot = repositoryRoots[0]!;
            var canonicalRoot = Path.GetFullPath(repositoryRoot);
            var canonicalSource = Path.GetFullPath(
                Path.IsPathRooted(sourcePath)
                    ? sourcePath
                    : Path.Combine(canonicalRoot, sourcePath));
            var canonicalOwner = Path.GetFullPath(Path.Combine(
                canonicalRoot,
                AllowedProcessOwnerPath.Replace('/', Path.DirectorySeparatorChar)));
            return string.Equals(canonicalSource, canonicalOwner, StringComparison.Ordinal);
        }
        catch (Exception failure) when (
            failure is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return false;
        }
    }
}
