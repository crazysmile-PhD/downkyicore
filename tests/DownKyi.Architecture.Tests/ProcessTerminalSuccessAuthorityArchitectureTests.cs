using System.Reflection;
using DownKyi.ProcessSupervision;

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
        var mutation = Environment.GetEnvironmentVariable(
            MutationEnvironmentVariable);
        var contract = ProcessTerminalSuccessSemanticGate.AnalyzeContract(
            RepositoryRoot,
            mutation);

        AssertNoFindings(contract.CompilationErrors);
        AssertNoFindings(contract.AuthorityViolations);

        var production = ProcessTerminalSuccessSemanticGate.AnalyzeProduction(
            RepositoryRoot,
            mutation);

        AssertNoFindings(production.CompilationErrors);
        AssertNoFindings(production.AuthorityViolations);
    }

    private static void AssertNoFindings(IReadOnlyList<string> findings)
    {
        Assert.True(
            findings.Count == 0,
            $"Unexpected semantic-gate findings:{Environment.NewLine}" +
            string.Join(Environment.NewLine, findings));
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
