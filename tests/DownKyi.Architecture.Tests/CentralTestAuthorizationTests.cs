using System.Security.Cryptography;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestAuthorizationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public async Task ExactOneShotAuthorizationReachesTheIntendedTestAssembly()
    {
        using var result = await RunAuthorizedChildAsync(
                CentralTestAuthorizationMutation.None)
            .ConfigureAwait(true);

        Assert.Equal(0, result.Outcome.ExitCode);
        Assert.True(result.Outcome.TreeQuiescent);
        Assert.True(result.Outcome.Ownership.OwnershipEstablished);
        var report = CentralTestExecutionValidator.ValidateExpectedExecution(
            result.Outcome.ExitCode,
            result.TrxPath,
            [typeof(CentralTestAuthorizationExecutionFixture).FullName!]);
        Assert.Equal(1, report.PassedExpectedClasses);
    }

    [Fact]
    public async Task WrongTokenFailsClosed()
    {
        using var result = await RunAuthorizedChildAsync(
                CentralTestAuthorizationMutation.WrongToken)
            .ConfigureAwait(true);

        Assert.NotEqual(0, result.Outcome.ExitCode);
        Assert.True(result.Outcome.TreeQuiescent);
        Assert.Contains(
            "must execute through the central in-process test runner",
            result.Outcome.StandardError + result.Outcome.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(result.TrxPath));
    }

    [Fact]
    public async Task NumericHandleOrFileDescriptorStringIsNotAuthorization()
    {
        var paths = CreateInvocation();
        using var resultCleanup = new ResultDirectoryCleanup(paths.TrxPath);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CentralTestAuthorization.EndpointEnvironmentVariable] = null,
            [CentralTestAuthorization.TokenEnvironmentVariable] = Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(32)),
            [CentralTestAuthorization.LegacyPipeEnvironmentVariable] = "5566"
        };
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec(
                    "dotnet",
                    paths.Arguments,
                    RepositoryRoot,
                    environment,
                    closeStandardInput: true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);

        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
        Assert.Contains(
            "must execute through the central in-process test runner",
            outcome.StandardError + outcome.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.TrxPath));
    }

    private static async Task<AuthorizedChildResult> RunAuthorizedChildAsync(
        CentralTestAuthorizationMutation mutation)
    {
        var paths = CreateInvocation();
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            mutation);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec(
                    "dotnet",
                    paths.Arguments,
                    RepositoryRoot,
                    environment,
                    closeStandardInput: true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);

        await authorization.CompleteAsync(
                budget,
                lease.TargetExitedToken,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return new AuthorizedChildResult(outcome, paths.TrxPath);
    }

    private static InvocationPaths CreateInvocation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-authorization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var trxPath = Path.Combine(directory, "authorization.trx");
        return new InvocationPaths(CreateArguments(trxPath), trxPath);
    }

    private static string[] CreateArguments(string trxPath) =>
    [
        typeof(CentralTestAuthorizationTests).Assembly.Location,
        "-noLogo",
        "-noColor",
        "-noAutoReporters",
        "-reporter",
        "quiet",
        "-parallel",
        "none",
        "-class",
        typeof(CentralTestAuthorizationExecutionFixture).FullName!,
        "-trx",
        trxPath
    ];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "Could not locate the DownKyi repository root.");
    }

    private sealed record InvocationPaths(string[] Arguments, string TrxPath);

    private sealed class ResultDirectoryCleanup(string trxPath) : IDisposable
    {
        public void Dispose()
        {
            var directory = Path.GetDirectoryName(trxPath);
            if (directory != null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal sealed record AuthorizedChildResult(
    OwnedProcessOutcome Outcome,
    string TrxPath) : IDisposable
{
    public void Dispose()
    {
        var directory = Path.GetDirectoryName(TrxPath);
        if (directory != null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class CentralTestAuthorizationExecutionFixture
{
    [Fact]
    public void AuthorizedInvocationReachesTestCode()
    {
        Assert.True(Environment.ProcessId > 0);
    }
}
