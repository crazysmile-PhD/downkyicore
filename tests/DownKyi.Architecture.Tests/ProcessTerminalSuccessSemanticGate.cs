using DownKyi.ProcessSupervision;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DownKyi.Architecture.Tests;

internal sealed record ProcessTerminalSuccessSemanticGateResult(
    IReadOnlyList<string> CompilationErrors,
    IReadOnlyList<string> AuthorityViolations);

internal static class ProcessTerminalSuccessSemanticGate
{
    private const string ContractNamespace = "DownKyi.ProcessSupervision";

    internal static ProcessTerminalSuccessSemanticGateResult AnalyzeContract(
        string repositoryRoot,
        string? mutation)
    {
        var compilation = ProcessTerminalSuccessSemanticCompilation
            .CreateContract(repositoryRoot, mutation);
        var violations = new List<string>();
        violations.AddRange(FindFormalGateViolations(compilation, repositoryRoot));
        violations.AddRange(FindOutcomeConstructionConsumers(
            compilation,
            repositoryRoot));
        violations.AddRange(FindSuccessAuthorityViolations(
            compilation,
            repositoryRoot,
            allowContractMembers: true));
        violations.AddRange(FindContractOperationConsumerViolations(
            compilation,
            repositoryRoot));
        violations.AddRange(FindDynamicViolations(compilation, repositoryRoot));
        return new ProcessTerminalSuccessSemanticGateResult(
            ProcessTerminalSuccessSemanticCompilation.CompilationErrors(
                compilation),
            violations.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static ProcessTerminalSuccessSemanticGateResult AnalyzeProduction(
        string repositoryRoot,
        string? mutation)
    {
        var (compilation, mutationTree) = ProcessTerminalSuccessSemanticCompilation
            .CreateProduction(repositoryRoot, mutation);
        var violations = FindProductionContractReferences(
            compilation,
            repositoryRoot);
        violations.AddRange(FindOutcomeConstructionConsumers(
            compilation,
            repositoryRoot));
        violations.AddRange(FindSuccessAuthorityViolations(
            compilation,
            repositoryRoot,
            allowContractMembers: false));
        violations.AddRange(FindDynamicViolations(compilation, repositoryRoot));
        return new ProcessTerminalSuccessSemanticGateResult(
            mutationTree is null
                ? []
                : ProcessTerminalSuccessSemanticCompilation.CompilationErrors(
                    compilation,
                    mutationTree),
            violations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static List<string> FindFormalGateViolations(
        CSharpCompilation compilation,
        string repositoryRoot)
    {
        var references = FindSimpleNameReferences(
                compilation,
                nameof(ProcessSupervisionProof.FormalGatePassed))
            .Where(reference =>
                reference.Symbol is IPropertySymbol property &&
                IsContractType(
                    property.ContainingType,
                    nameof(ProcessSupervisionProof)))
            .ToArray();
        var violations = references
            .Where(reference => reference.Enclosing is not IPropertySymbol property ||
                                !IsFinalSuccessMember(property))
            .Select(reference => Describe(
                repositoryRoot,
                reference.Node,
                "FormalGatePassed is consumed outside ProcessSupervisionOutcome.Succeeded"))
            .ToList();
        if (references.Length != 1)
        {
            violations.Add(
                $"FormalGatePassed must have exactly one contract consumer; actual={references.Length}.");
        }

        return violations;
    }

    private static List<string> FindOutcomeConstructionConsumers(
        CSharpCompilation compilation,
        string repositoryRoot)
    {
        var violations = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var creation in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<ExpressionSyntax>()
                         .Where(node => node is ObjectCreationExpressionSyntax or
                             ImplicitObjectCreationExpressionSyntax))
            {
                if (model.GetOperation(creation) is IObjectCreationOperation
                    { Constructor.ContainingType: { } containingType } &&
                    IsContractType(
                        containingType,
                        nameof(ProcessSupervisionOutcome)))
                {
                    violations.Add(Describe(
                        repositoryRoot,
                        creation,
                        "constructs ProcessSupervisionOutcome outside its future owner"));
                }
            }
        }

        return violations;
    }

    private static List<string> FindSuccessAuthorityViolations(
        CSharpCompilation compilation,
        string repositoryRoot,
        bool allowContractMembers)
    {
        var successMembers = AllTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => allowContractMembers ||
                           type.ContainingNamespace.ToDisplayString() ==
                           ContractNamespace)
            .SelectMany(type => type.GetMembers())
            .Where(IsBooleanSuccessMember)
            .ToArray();
        var violations = new List<string>();
        foreach (var member in successMembers)
        {
            if (allowContractMembers && IsAllowedContractSuccessMember(member))
            {
                continue;
            }

            var syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?
                .GetSyntax();
            violations.Add(syntax is null
                ? $"declares competing success member {member.ToDisplayString()}"
                : Describe(
                    repositoryRoot,
                    syntax,
                    $"declares competing success member {member.ToDisplayString()}"));
        }

        if (allowContractMembers && successMembers.Count(IsFinalSuccessMember) != 1)
        {
            violations.Add(
                "ProcessSupervisionOutcome.Succeeded must be the single final-success member.");
        }

        return violations;
    }

    private static List<string> FindContractOperationConsumerViolations(
        CSharpCompilation compilation,
        string repositoryRoot)
    {
        var trackedTypes = new HashSet<string>(
        [
            nameof(ProcessContainmentOperationResult),
            nameof(ProcessContainmentOperationCompleted),
            nameof(ProcessContainmentOperationRejected)
        ], StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var reference in FindSimpleNameReferences(compilation))
        {
            if (reference.Symbol is INamedTypeSymbol type &&
                trackedTypes.Contains(type.Name) &&
                IsContractType(type, type.Name) &&
                !IsAllowedContractOperationUsage(type, reference))
            {
                violations.Add(Describe(
                    repositoryRoot,
                    reference.Node,
                    $"operation fact {type.Name} is consumed by " +
                    $"{reference.Enclosing?.ToDisplayString() ?? "<unknown>"}"));
            }
        }

        return violations;
    }

    private static List<string> FindDynamicViolations(
        CSharpCompilation compilation,
        string repositoryRoot)
    {
        var violations = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeSyntax>())
            {
                if (model.GetTypeInfo(type).Type?.TypeKind == TypeKind.Dynamic)
                {
                    violations.Add(Describe(
                        repositoryRoot,
                        type,
                        "uses dynamic in the process-supervision contract boundary"));
                }
            }

            foreach (var expression in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<ExpressionSyntax>())
            {
                if (model.GetOperation(expression) is
                    IDynamicMemberReferenceOperation or
                    IDynamicInvocationOperation or
                    IDynamicIndexerAccessOperation or
                    IDynamicObjectCreationOperation)
                {
                    violations.Add(Describe(
                        repositoryRoot,
                        expression,
                        "uses dynamic member or object access"));
                }
            }
        }

        return violations;
    }

    private static List<string> FindProductionContractReferences(
        CSharpCompilation compilation,
        string repositoryRoot)
    {
        var trackedNames = TrackedAuthoritySymbolNames();
        var contractAssemblyName =
            typeof(ProcessSupervisionOutcome).Assembly.GetName().Name;
        var violations = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<SimpleNameSyntax>())
            {
                var symbol = ReferencedSymbol(model, name);
                if (symbol is not null &&
                    (trackedNames.Contains(symbol.Name) ||
                     trackedNames.Contains(symbol.ContainingType?.Name ?? "")) &&
                    string.Equals(
                        symbol.ContainingAssembly?.Name,
                        contractAssemblyName,
                        StringComparison.Ordinal))
                {
                    violations.Add(Describe(
                        repositoryRoot,
                        name,
                        $"references {symbol.ToDisplayString()}"));
                }
            }
        }

        return violations;
    }

    private static IEnumerable<SemanticReference> FindSimpleNameReferences(
        CSharpCompilation compilation,
        string? name = null)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot()
                         .DescendantNodes()
                         .OfType<SimpleNameSyntax>()
                         .Where(candidate => name is null ||
                             candidate.Identifier.ValueText == name))
            {
                yield return new SemanticReference(
                    node,
                    ReferencedSymbol(model, node),
                    EnclosingMember(model, node));
            }
        }
    }

    private static bool IsAllowedContractSuccessMember(ISymbol member)
    {
        return IsFinalSuccessMember(member) ||
               member is IPropertySymbol
               { Name: "Succeeded" } && IsContractType(
                   member.ContainingType,
                   "SupervisorProtocolEncodeResult");
    }

    private static bool IsFinalSuccessMember(ISymbol member)
    {
        return member is IPropertySymbol
        { Name: nameof(ProcessSupervisionOutcome.Succeeded) } && IsContractType(
            member.ContainingType,
            nameof(ProcessSupervisionOutcome));
    }

    private static bool IsBooleanSuccessMember(ISymbol member)
    {
        if (!IsSuccessName(member.Name))
        {
            return false;
        }

        return member switch
        {
            IPropertySymbol property => IsBoolean(property.Type),
            IFieldSymbol field => IsBoolean(field.Type),
            IMethodSymbol method when method.MethodKind is not
                MethodKind.PropertyGet and not MethodKind.PropertySet and not
                MethodKind.EventAdd and not MethodKind.EventRemove =>
                IsBoolean(method.ReturnType),
            IEventSymbol eventSymbol when eventSymbol.Type is INamedTypeSymbol type =>
                IsBoolean(type.DelegateInvokeMethod?.ReturnType),
            _ => false
        };
    }

    private static bool IsBoolean(ITypeSymbol? type) =>
        type is { SpecialType: SpecialType.System_Boolean };
    private static bool IsSuccessName(string name)
        => name.Contains("Success", StringComparison.Ordinal) ||
           name.Contains("Succeed", StringComparison.Ordinal);

    private static bool IsAllowedContractOperationUsage(
        INamedTypeSymbol referencedType,
        SemanticReference reference)
    {
        if (reference.Enclosing is INamedTypeSymbol type &&
            IsContractType(type, type.Name) &&
            reference.Node.AncestorsAndSelf().Any(node => node is BaseTypeSyntax))
        {
            return (type.Name, referencedType.Name) is
                (nameof(ProcessContainmentOperationCompleted),
                    nameof(ProcessContainmentOperationResult)) or
                (nameof(ProcessContainmentOperationRejected),
                    nameof(ProcessContainmentOperationResult)) or
                ("PublishedOperationCompleted",
                    nameof(ProcessContainmentOperationCompleted)) or
                ("PublishedOperationRejected",
                    nameof(ProcessContainmentOperationRejected));
        }

        return reference.Enclosing is IMethodSymbol method &&
               IsContractType(
                   method.ContainingType,
                   nameof(ProcessContainmentOperationAuthority)) &&
               IsMethodReturnTypeReference(method, reference.Node) &&
               referencedType.Name == nameof(ProcessContainmentOperationResult) &&
               HasAllowedOperationFactorySignature(method);
    }

    private static bool HasAllowedOperationFactorySignature(IMethodSymbol method)
    {
        var parameterTypes = method.Parameters
            .Select(parameter => parameter.Type.ToDisplayString())
            .ToArray();
        if (method.Name == "FromBackend")
        {
            return parameterTypes.SequenceEqual(
                [
                    $"{ContractNamespace}.ProcessContainmentBackendResult",
                    "System.Collections.Generic.IEnumerable<" +
                    $"{ContractNamespace}.ProcessCleanupFailure>"
                ],
                StringComparer.Ordinal);
        }

        return method.Name == "Rejected" &&
               parameterTypes.Length == 2 &&
               parameterTypes[0] is
                   $"{ContractNamespace}.ProcessContainmentCallerFailure" or
                   $"{ContractNamespace}.ProcessContainmentContractFailure" &&
               parameterTypes[1] ==
                   "System.Collections.Generic.IEnumerable<" +
                   $"{ContractNamespace}.ProcessCleanupFailure>";
    }

    private static bool IsMethodReturnTypeReference(
        IMethodSymbol method,
        SyntaxNode node)
    {
        var declaration = method.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<MethodDeclarationSyntax>()
            .SingleOrDefault();
        return declaration?.ReturnType.Span.Contains(node.Span) is true;
    }

    private static bool IsContractType(INamedTypeSymbol type, string name)
    {
        return type.Name == name &&
               type.ContainingType is null &&
               type.ContainingNamespace.ToDisplayString() == ContractNamespace;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(
        INamespaceOrTypeSymbol root)
    {
        foreach (var type in root.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in AllTypes(type))
            {
                yield return nested;
            }
        }

        if (root is not INamespaceSymbol namespaceSymbol)
        {
            yield break;
        }

        foreach (var child in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in AllTypes(child))
            {
                yield return type;
            }
        }
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

    private static ISymbol? ReferencedSymbol(
        SemanticModel model,
        SyntaxNode node)
    {
        var symbolInfo = model.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length == 1
            ? symbolInfo.CandidateSymbols[0]
            : null);
        return symbol is IAliasSymbol alias ? alias.Target : symbol;
    }

    private static ISymbol? EnclosingMember(
        SemanticModel model,
        SyntaxNode node)
    {
        var declaration = node.AncestorsAndSelf()
            .OfType<MemberDeclarationSyntax>()
            .FirstOrDefault();
        var enclosing = declaration is null
            ? model.GetEnclosingSymbol(node.SpanStart)
            : model.GetDeclaredSymbol(declaration);
        return enclosing is IMethodSymbol { AssociatedSymbol: not null } method
            ? method.AssociatedSymbol
            : enclosing;
    }

    private static string Describe(
        string repositoryRoot,
        SyntaxNode node,
        string detail)
    {
        var tree = node.SyntaxTree;
        var line = tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
        var path = tree.FilePath.StartsWith("__", StringComparison.Ordinal)
            ? tree.FilePath
            : Path.GetRelativePath(repositoryRoot, tree.FilePath);
        return $"{path}:{line} {detail}";
    }

    private sealed record SemanticReference(
        SyntaxNode Node,
        ISymbol? Symbol,
        ISymbol? Enclosing);
}
