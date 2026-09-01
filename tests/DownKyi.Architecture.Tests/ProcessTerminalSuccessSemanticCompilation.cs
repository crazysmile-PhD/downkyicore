using DownKyi.ProcessSupervision;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DownKyi.Architecture.Tests;

internal static class ProcessTerminalSuccessSemanticCompilation
{
    internal static CSharpCompilation CreateContract(
        string repositoryRoot,
        string? mutation)
    {
        ValidateMutation(mutation);
        var contractDirectory = Path.Combine(
            repositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision");
        var syntaxTrees = Directory.EnumerateFiles(
                contractDirectory,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => IsSourcePath(Path.GetRelativePath(
                contractDirectory,
                path)))
            .Order(StringComparer.Ordinal)
            .Select(ParseFile)
            .Prepend(ImplicitUsings())
            .ToList();
        var mutationTree = CreateContractMutationTree(mutation);
        if (mutationTree is not null)
        {
            syntaxTrees.Add(mutationTree);
        }

        return CSharpCompilation.Create(
            "DownKyi.ProcessSupervision.ArchitectureInspection",
            syntaxTrees,
            TrustedPlatformReferences(),
            CompilationOptions());
    }

    internal static (CSharpCompilation Compilation, SyntaxTree? MutationTree)
        CreateProduction(
            string repositoryRoot,
            string? mutation)
    {
        ValidateMutation(mutation);
        var syntaxTrees = EnumerateProductionSourcePaths(repositoryRoot)
            .Select(ParseFile)
            .ToList();
        var mutationTree = CreateProductionMutationTree(mutation);
        if (mutationTree is not null)
        {
            syntaxTrees.Add(mutationTree);
        }

        var references = TrustedPlatformReferences()
            .Append(MetadataReference.CreateFromFile(
                typeof(ProcessSupervisionOutcome).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "DownKyi.Architecture.Tests",
            syntaxTrees,
            references,
            CompilationOptions());
        return (compilation, mutationTree);
    }

    internal static IReadOnlyList<string> CompilationErrors(
        CSharpCompilation compilation,
        SyntaxTree? tree = null)
    {
        return compilation.GetDiagnostics()
            .Where(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error &&
                (tree is null || diagnostic.Location.SourceTree == tree))
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
    }

    private static IEnumerable<string> EnumerateProductionSourcePaths(
        string repositoryRoot)
    {
        var contractDirectory = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision"));
        return Directory.EnumerateFiles(
                repositoryRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).StartsWith(
                contractDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => IsProductionPath(
                Path.GetRelativePath(repositoryRoot, path)))
            .Order(StringComparer.Ordinal);
    }

    private static bool IsProductionPath(string relativePath)
    {
        return IsSourcePath(relativePath) &&
               !relativePath.Split(
                       [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                       StringSplitOptions.RemoveEmptyEntries)
                   .Any(segment => segment.Equals(
                       "tests",
                       StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSourcePath(string relativePath)
    {
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        string[] excludedSegments =
        [
            ".git",
            ".tools",
            "artifacts",
            "benchmarks",
            "bin",
            "obj"
        ];
        return !segments.Any(segment => excludedSegments.Contains(
            segment,
            StringComparer.OrdinalIgnoreCase));
    }

    private static SyntaxTree? CreateProductionMutationTree(string? mutation)
    {
        var source = mutation switch
        {
            "direct-formal-gate" =>
                "using DownKyi.ProcessSupervision; namespace MutatedProduction; " +
                "public static class DirectFormalGateConsumer { public static bool " +
                "FinalSuccess(ProcessSupervisionProof proof) => proof.FormalGatePassed; }",
            "second-success-authority" =>
                "namespace DownKyi.ProcessSupervision; public sealed class " +
                "InjectedSecondSuccessAuthority { public bool Succeeded => true; }",
            "operation-completed-final-success" =>
                "using DownKyi.ProcessSupervision; namespace MutatedProduction; " +
                "internal static class OperationCompletedConsumer { internal static bool " +
                "FinalSuccess(ProcessContainmentOperationResult result) => " +
                "result is ProcessContainmentOperationCompleted; }",
            _ => null
        };
        return MutationTree(source, mutation);
    }

    private static SyntaxTree? CreateContractMutationTree(string? mutation)
    {
        var source = mutation switch
        {
            "target-typed-outcome-constructor" =>
                "namespace DownKyi.ProcessSupervision; internal static class " +
                "InjectedOutcomeConstructorConsumer { internal static " +
                "ProcessSupervisionOutcome Construct(ProcessTerminalCandidate terminal, " +
                "ProcessSupervisionProof proof, IEnumerable<ProcessCleanupFailure> cleanup) " +
                "=> new(terminal, proof, cleanup); }",
            "qualified-bool-success-authority" =>
                "namespace DownKyi.ProcessSupervision; public static class " +
                "InjectedQualifiedBooleanAuthority { public static System.Boolean " +
                "Succeeded => true; }",
            "positional-record-success-authority" =>
                "namespace DownKyi.ProcessSupervision; public sealed record " +
                "InjectedPositionalAuthority(System.Boolean Succeeded);",
            "contract-operation-consumer" =>
                "namespace DownKyi.ProcessSupervision; internal static class " +
                "InjectedOperationConsumer { internal static bool Accept(" +
                "ProcessContainmentOperationResult result) => " +
                "result is ProcessContainmentOperationCompleted; }",
            "dynamic-success-access" =>
                "namespace DownKyi.ProcessSupervision; internal static class " +
                "InjectedDynamicSuccessConsumer { internal static bool Accept(" +
                "object outcome) => ((dynamic)outcome).Succeeded; }",
            _ => null
        };
        return MutationTree(source, mutation);
    }

    private static void ValidateMutation(string? mutation)
    {
        if (string.IsNullOrEmpty(mutation) ||
            CreateProductionMutationTree(mutation) is not null ||
            CreateContractMutationTree(mutation) is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Unsupported terminal-success mutation: {mutation}");
    }

    private static SyntaxTree? MutationTree(string? source, string? mutation)
    {
        return source is null
            ? null
            : CSharpSyntaxTree.ParseText(
                source,
                ParseOptions(),
                path: $"__mutation__/{mutation}.cs");
    }

    private static SyntaxTree ImplicitUsings()
    {
        return CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; " +
            "global using System.IO; global using System.Linq; " +
            "global using System.Net.Http; global using System.Threading; " +
            "global using System.Threading.Tasks;",
            ParseOptions(),
            path: "__architecture__/ImplicitUsings.g.cs");
    }

    private static SyntaxTree ParseFile(string path)
    {
        return CSharpSyntaxTree.ParseText(
            File.ReadAllText(path),
            ParseOptions(),
            path);
    }

    private static CSharpParseOptions ParseOptions()
    {
        return CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
    }

    private static CSharpCompilationOptions CompilationOptions()
    {
        return new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable);
    }

    private static IEnumerable<MetadataReference> TrustedPlatformReferences()
    {
        var trustedAssemblies = AppContext.GetData(
            "TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new InvalidOperationException(
                "Trusted platform assembly metadata is unavailable.");
        }

        return trustedAssemblies.Split(Path.PathSeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
    }
}
