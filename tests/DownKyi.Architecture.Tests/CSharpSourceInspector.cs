using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DownKyi.Architecture.Tests;

internal static class CSharpSourceInspector
{
    private static readonly ImmutableArray<MetadataReference> PlatformReferences =
        CreatePlatformReferences();

    public static IReadOnlyList<CSharpTypeDeclaration> ReadTypeDeclarations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = Parse(source);
        return root
            .DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(declaration =>
            {
                var namespaceName = string.Join(
                    ".",
                    declaration
                        .Ancestors()
                        .OfType<BaseNamespaceDeclarationSyntax>()
                        .Reverse()
                        .Select(item => item.Name.ToString()));
                var name = declaration.Identifier.ValueText;
                var fullName = string.IsNullOrEmpty(namespaceName)
                    ? name
                    : $"{namespaceName}.{name}";
                return new CSharpTypeDeclaration(name, fullName);
            })
            .ToArray();
    }

    public static bool DeclaresInterfaceWithNamespaceDependency(
        string source,
        string namespacePrefix)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePrefix);
        var root = Parse(source);
        var importsPresentation = root.Usings.Any(usingDirective =>
            StartsWithNamespace(usingDirective.Name?.ToString(), namespacePrefix));

        return root
            .DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>()
            .Any(declaration =>
                importsPresentation || declaration
                    .DescendantNodes()
                    .OfType<NameSyntax>()
                    .Any(name => StartsWithNamespace(name.ToString(), namespacePrefix)));
    }

    public static IReadOnlyList<string> FindSynchronousRuntimeOperations(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ArchitecturePolicyProbe",
            [syntaxTree],
            PlatformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetCompilationUnitRoot();
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            var fieldType = field.Declaration.Type is NullableTypeSyntax nullableType
                ? nullableType.ElementType.ToString()
                : field.Declaration.Type.ToString();
            if (field.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                fieldType.EndsWith("BilibiliHttpClient", StringComparison.Ordinal))
            {
                violations.Add("static BilibiliHttpClient field");
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            {
                continue;
            }

            var containingType = method.ContainingType?.ToDisplayString();
            var operation = (containingType, method.Name) switch
            {
                ("System.Net.Http.HttpClient", "Send") => "HttpClient.Send",
                ("System.IO.StreamReader", "ReadToEnd") => "StreamReader.ReadToEnd",
                ("System.Threading.WaitHandle", "WaitOne") => "WaitHandle.WaitOne",
                ("System.Threading.Tasks.Task", "Wait") => "Task.Wait",
                _ => null
            };
            if (operation == null &&
                method.Name == "GetResult" &&
                method.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices" &&
                method.ContainingType?.Name == "TaskAwaiter")
            {
                operation = "TaskAwaiter.GetResult";
            }
            if (operation != null)
            {
                violations.Add(operation);
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static CompilationUnitSyntax Parse(string source)
    {
        return CSharpSyntaxTree
            .ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview))
            .GetCompilationUnitRoot();
    }

    private static bool StartsWithNamespace(string? value, string namespacePrefix)
    {
        return value != null &&
               (value.Equals(namespacePrefix, StringComparison.Ordinal) ||
                value.StartsWith($"{namespacePrefix}.", StringComparison.Ordinal));
    }

    private static ImmutableArray<MetadataReference> CreatePlatformReferences()
    {
        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(trustedAssemblies))
        {
            throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        }

        return trustedAssemblies
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }
}

internal sealed record CSharpTypeDeclaration(string Name, string FullName);
