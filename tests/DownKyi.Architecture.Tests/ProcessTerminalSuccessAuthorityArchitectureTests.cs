using System.Reflection;
using DownKyi.ProcessSupervision;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessTerminalSuccessAuthorityArchitectureTests
{
    private const string MutationEnvironmentVariable =
        "DOWNKYI_TEST_MUTATE_TERMINAL_SUCCESS_AUTHORITY";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CompiledContractHasOneDerivedFinalSuccessMember()
    {
        var contractAssembly = typeof(ProcessSupervisionOutcome).Assembly;
        var successMembers = contractAssembly.GetTypes()
            .SelectMany(SuccessBooleanMembers)
            .ToArray();

        Assert.Equal(
            [
                "DownKyi.ProcessSupervision.ProcessSupervisionOutcome.Succeeded",
                "DownKyi.ProcessSupervision.SupervisorProtocolEncodeResult.Succeeded"
            ],
            successMembers
                .Select(member =>
                    $"{member.DeclaringType?.FullName}.{member.Name}")
                .Order(StringComparer.Ordinal));
        var succeeded = Assert.IsAssignableFrom<PropertyInfo>(Assert.Single(
            successMembers,
            member => member.DeclaringType == typeof(ProcessSupervisionOutcome)));
        Assert.Equal(nameof(ProcessSupervisionOutcome.Succeeded), succeeded.Name);
        Assert.Equal(typeof(ProcessSupervisionOutcome), succeeded.DeclaringType);
        Assert.True(succeeded.GetMethod?.IsPublic);

        var constructor = Assert.Single(
            typeof(ProcessSupervisionOutcome).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsAssembly);
        Assert.Equal(
            [
                typeof(ProcessTerminalCandidate),
                typeof(ProcessSupervisionProof),
                typeof(IEnumerable<ProcessCleanupFailure>)
            ],
            constructor.GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void SemanticGateKeepsProofAndOperationFactsOutOfProduction()
    {
        var contractCompilation = CreateContractCompilation();
        AssertNoCompilationErrors(contractCompilation);
        AssertFormalGateIsConsumedOnlyBySucceeded(contractCompilation);
        AssertNoOutcomeConstructionConsumer(contractCompilation);

        var violations = FindProductionAuthorityViolations();

        Assert.Empty(violations);
    }

    private static IEnumerable<MemberInfo> SuccessBooleanMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance |
                                   BindingFlags.Static |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly;
        return type.GetProperties(flags)
            .Where(property =>
                property.PropertyType == typeof(bool) &&
                IsSuccessName(property.Name))
            .Cast<MemberInfo>()
            .Concat(type.GetMethods(flags)
                .Where(method =>
                    !method.IsSpecialName &&
                    method.ReturnType == typeof(bool) &&
                    IsSuccessName(method.Name)))
            .Concat(type.GetFields(flags)
                .Where(field =>
                    field.FieldType == typeof(bool) &&
                    IsSuccessName(field.Name)));
    }

    private static bool IsSuccessName(string name)
    {
        return name.Contains("Success", StringComparison.Ordinal) ||
               name.Contains("Succeed", StringComparison.Ordinal);
    }

    private static CSharpCompilation CreateContractCompilation()
    {
        var contractDirectory = Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision");
        var syntaxTrees = Directory.EnumerateFiles(
                contractDirectory,
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(ParseFile)
            .Prepend(CSharpSyntaxTree.ParseText(
                """
                global using System;
                global using System.Collections.Generic;
                global using System.IO;
                global using System.Linq;
                global using System.Net.Http;
                global using System.Threading;
                global using System.Threading.Tasks;
                """,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                path: "__architecture__/ImplicitUsings.g.cs"));
        return CSharpCompilation.Create(
            "DownKyi.ProcessSupervision.ArchitectureInspection",
            syntaxTrees,
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private static void AssertNoCompilationErrors(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();

        Assert.Empty(errors);
    }

    private static void AssertFormalGateIsConsumedOnlyBySucceeded(
        CSharpCompilation compilation)
    {
        var references = compilation.SyntaxTrees
            .SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot()
                    .DescendantNodes()
                    .OfType<SimpleNameSyntax>()
                    .Where(name => name.Identifier.ValueText ==
                                   nameof(ProcessSupervisionProof.FormalGatePassed))
                    .Select(name => new
                    {
                        Symbol = ReferencedSymbol(model, name),
                        Enclosing = EnclosingMember(model, name.SpanStart)
                    });
            })
            .Where(reference => reference.Symbol is IPropertySymbol property &&
                                property.ContainingType.Name ==
                                    nameof(ProcessSupervisionProof))
            .ToArray();

        var reference = Assert.Single(references);
        var property = Assert.IsAssignableFrom<IPropertySymbol>(reference.Enclosing);
        Assert.Equal(nameof(ProcessSupervisionOutcome.Succeeded), property.Name);
        Assert.Equal(nameof(ProcessSupervisionOutcome), property.ContainingType.Name);
    }

    private static void AssertNoOutcomeConstructionConsumer(
        CSharpCompilation compilation)
    {
        var consumers = compilation.SyntaxTrees
            .SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot()
                    .DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>()
                    .Select(creation => ReferencedSymbol(model, creation));
            })
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Constructor &&
                             method.ContainingType.Name ==
                                 nameof(ProcessSupervisionOutcome))
            .ToArray();

        Assert.Empty(consumers);
    }

    private static List<string> FindProductionAuthorityViolations()
    {
        var syntaxTrees = EnumerateProductionSourcePaths()
            .Select(ParseFile)
            .ToList();
        var mutationTree = CreateMutationTree();
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
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        if (mutationTree is not null)
        {
            var mutationErrors = compilation.GetDiagnostics()
                .Where(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error &&
                    diagnostic.Location.SourceTree == mutationTree)
                .Select(diagnostic => diagnostic.ToString())
                .ToArray();
            Assert.Empty(mutationErrors);
        }

        var trackedNames = TrackedAuthoritySymbolNames();
        var contractAssemblyName =
            typeof(ProcessSupervisionOutcome).Assembly.GetName().Name;
        var violations = new List<string>();
        foreach (var tree in syntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<SimpleNameSyntax>()
                         .Where(name => trackedNames.Contains(
                             name.Identifier.ValueText)))
            {
                var symbol = ReferencedSymbol(model, name);
                if (symbol is not null &&
                    string.Equals(
                        symbol.ContainingAssembly?.Name,
                        contractAssemblyName,
                        StringComparison.Ordinal))
                {
                    violations.Add(DescribeViolation(
                        tree,
                        name,
                        $"references {symbol.ToDisplayString()}"));
                }
            }

            foreach (var member in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<MemberDeclarationSyntax>()
                         .Where(IsCompetingSuccessDeclaration))
            {
                var symbol = model.GetDeclaredSymbol(member);
                if (symbol?.ContainingNamespace?.ToDisplayString() ==
                    "DownKyi.ProcessSupervision")
                {
                    violations.Add(DescribeViolation(
                        tree,
                        member,
                        $"declares competing success member {symbol.ToDisplayString()}"));
                }
            }
        }

        return violations;
    }

    private static IEnumerable<string> EnumerateProductionSourcePaths()
    {
        var contractDirectory = Path.GetFullPath(Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision"));
        return Directory.EnumerateFiles(
                RepositoryRoot,
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).StartsWith(
                contractDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => IsProductionPath(
                Path.GetRelativePath(RepositoryRoot, path)))
            .Order(StringComparer.Ordinal);
    }

    private static bool IsProductionPath(string relativePath)
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
            "obj",
            "tests"
        ];
        return !segments.Any(segment => excludedSegments.Contains(
            segment,
            StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> TrackedAuthoritySymbolNames()
    {
        Type[] operationTypes =
        [
            typeof(ProcessContainmentOperationAuthority),
            typeof(ProcessContainmentOperationResult),
            typeof(ProcessContainmentOperationCompleted),
            typeof(ProcessContainmentOperationRejected),
            typeof(ProcessContainmentOperationFailure),
            typeof(ProcessContainmentBackendResult),
            typeof(ProcessContainmentBackendFailure),
            typeof(ProcessContainmentCallerFailure),
            typeof(ProcessContainmentContractFailure)
        ];
        return operationTypes
            .Select(type => type.Name)
            .Append(nameof(ProcessSupervisionOutcome))
            .Append(nameof(ProcessSupervisionOutcome.Succeeded))
            .Append(nameof(ProcessSupervisionProof.FormalGatePassed))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsCompetingSuccessDeclaration(
        MemberDeclarationSyntax declaration)
    {
        return declaration switch
        {
            PropertyDeclarationSyntax property =>
                IsBooleanType(property.Type) &&
                IsSuccessName(property.Identifier.ValueText),
            MethodDeclarationSyntax method =>
                IsBooleanType(method.ReturnType) &&
                IsSuccessName(method.Identifier.ValueText),
            FieldDeclarationSyntax field =>
                IsBooleanType(field.Declaration.Type) &&
                field.Declaration.Variables.Any(variable =>
                    IsSuccessName(variable.Identifier.ValueText)),
            _ => false
        };
    }

    private static bool IsBooleanType(TypeSyntax type)
    {
        return type is PredefinedTypeSyntax predefined &&
               predefined.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.BoolKeyword);
    }

    private static SyntaxTree? CreateMutationTree()
    {
        var mutation = Environment.GetEnvironmentVariable(
            MutationEnvironmentVariable);
        var source = mutation switch
        {
            null or "" => null,
            "direct-formal-gate" =>
                """
                using DownKyi.ProcessSupervision;
                namespace MutatedProduction;
                public static class DirectFormalGateConsumer
                {
                    public static bool FinalSuccess(ProcessSupervisionProof proof) =>
                        proof.FormalGatePassed;
                }
                """,
            "second-success-authority" =>
                """
                namespace DownKyi.ProcessSupervision;
                public sealed class InjectedSecondSuccessAuthority
                {
                    public bool Succeeded => true;
                }
                """,
            "operation-completed-final-success" =>
                """
                using DownKyi.ProcessSupervision;
                namespace MutatedProduction;
                internal static class OperationCompletedConsumer
                {
                    internal static bool FinalSuccess(
                        ProcessContainmentOperationResult result) =>
                        result is ProcessContainmentOperationCompleted;
                }
                """,
            _ => throw new InvalidOperationException(
                $"Unsupported terminal-success mutation: {mutation}")
        };
        return source is null
            ? null
            : CSharpSyntaxTree.ParseText(
                source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                path: $"__mutation__/{mutation}.cs");
    }

    private static SyntaxTree ParseFile(string path)
    {
        return CSharpSyntaxTree.ParseText(
            File.ReadAllText(path),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            path);
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

    private static ISymbol? ReferencedSymbol(
        SemanticModel model,
        SyntaxNode node)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.SingleOrDefault();
    }

    private static ISymbol? EnclosingMember(
        SemanticModel model,
        int position)
    {
        var enclosing = model.GetEnclosingSymbol(position);
        return enclosing is IMethodSymbol { AssociatedSymbol: not null } method
            ? method.AssociatedSymbol
            : enclosing;
    }

    private static string DescribeViolation(
        SyntaxTree tree,
        SyntaxNode node,
        string detail)
    {
        var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        var path = string.IsNullOrWhiteSpace(tree.FilePath)
            ? "<source>"
            : tree.FilePath.StartsWith("__mutation__", StringComparison.Ordinal)
                ? tree.FilePath
                : Path.GetRelativePath(RepositoryRoot, tree.FilePath);
        return $"{path}:{line} {detail}";
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "Could not locate the DownKyi repository root.");
    }
}
