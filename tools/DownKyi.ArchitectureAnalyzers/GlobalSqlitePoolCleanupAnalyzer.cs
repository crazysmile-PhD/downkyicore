using System;
using System.Collections.Immutable;
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
                        operationContext.Operation.Syntax.SyntaxTree.FilePath))
                {
                    operationContext.ReportDiagnostic(Diagnostic.Create(
                        Rule,
                        operationContext.Operation.Syntax.GetLocation()));
                }
            }, OperationKind.Invocation, OperationKind.MethodReference);
        });
    }

    private static bool IsAllowedProcessOwner(Compilation compilation, string sourcePath)
    {
        var normalizedPath = sourcePath.Replace('\\', '/');
        return string.Equals(
                   compilation.AssemblyName,
                   AllowedProcessOwnerAssembly,
                   StringComparison.Ordinal) &&
               (string.Equals(
                    normalizedPath,
                    AllowedProcessOwnerPath,
                    StringComparison.Ordinal) ||
                normalizedPath.EndsWith(
                    "/" + AllowedProcessOwnerPath,
                    StringComparison.Ordinal));
    }
}
