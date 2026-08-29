using System.Diagnostics;
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
        using var result = await RunAuthorizedChildAsync(CentralTestAuthorizationMutation.None)
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
    public Task WrongTokenFailsClosed() =>
        AssertInvalidAuthorizationAsync(CentralTestAuthorizationMutation.WrongToken);

    [Fact]
    public Task WrongInvocationHashFailsClosed() =>
        AssertInvalidAuthorizationAsync(CentralTestAuthorizationMutation.WrongInvocationHash);

    [Fact]
    public Task AuthorizationReplayFailsClosed() =>
        AssertInvalidAuthorizationAsync(CentralTestAuthorizationMutation.Replay);

    [Fact]
    public Task PartialAuthorizationFailsClosed() =>
        AssertInvalidAuthorizationAsync(CentralTestAuthorizationMutation.Partial);

    private static async Task AssertInvalidAuthorizationAsync(
        CentralTestAuthorizationMutation mutation)
    {
        using var result = await RunAuthorizedChildAsync(mutation).ConfigureAwait(true);

        Assert.NotEqual(0, result.Outcome.ExitCode);
        Assert.True(result.Outcome.TreeQuiescent);
        Assert.Contains(
            "must execute through the central in-process test runner",
            result.Outcome.StandardError + result.Outcome.StandardOutput,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(result.TrxPath));
    }

    [Fact]
    public async Task AuthorizationEndpointEofFailsClosed()
    {
        var paths = CreateInvocation();
        using var resultCleanup = new ResultDirectoryCleanup(paths.TrxPath);
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec("dotnet", paths.Arguments, RepositoryRoot, environment, true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);

        await authorization.CloseAfterConnectionForTestingAsync(
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
        Assert.False(File.Exists(paths.TrxPath));
    }

    [Fact]
    public async Task AuthorizationSentToDifferentInvocationFailsClosed()
    {
        var paths = CreateInvocation();
        using var resultCleanup = new ResultDirectoryCleanup(paths.TrxPath);
        var differentArguments = paths.Arguments.ToArray();
        differentArguments[9] = typeof(TestRunnerPolicyArchitectureTests).FullName!;
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec("dotnet", differentArguments, RepositoryRoot, environment, true),
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

        Assert.NotEqual(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
        Assert.False(File.Exists(paths.TrxPath));
    }

    [Fact]
    public async Task AuthoritativeTargetExitEndsAuthorizationBeforeTheOperationBudget()
    {
        var paths = CreateInvocation();
        using var resultCleanup = new ResultDirectoryCleanup(paths.TrxPath);
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec("dotnet", ["--info"], RepositoryRoot, environment, true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        var elapsed = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => authorization.CompleteAsync(
                    budget,
                    lease.TargetExitedToken,
                    TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        elapsed.Stop();
        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains("exited before authorization completed", failure.Message, StringComparison.Ordinal);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Authorization waited {elapsed.Elapsed}.");
        Assert.True(
            budget.RemainingOperation > TimeSpan.FromSeconds(5),
            $"Authorization consumed the operation budget; remaining {budget.RemainingOperation}.");
        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.TreeQuiescent);
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
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec("dotnet", paths.Arguments, RepositoryRoot, environment, true),
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

    internal static async Task<AuthorizedChildResult> RunAuthorizedChildAsync(
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
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec("dotnet", paths.Arguments, RepositoryRoot, environment, true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);

        try
        {
            await authorization.CompleteAsync(
                    budget,
                    lease.TargetExitedToken,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        catch (IOException) when (mutation == CentralTestAuthorizationMutation.Replay)
        {
            // The guard is allowed to reject and close as soon as it observes replay bytes.
        }
        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        return new AuthorizedChildResult(outcome, paths.TrxPath);
    }

    internal static ProcessStartInfo CreateDirectStartInfo(string trxPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in CreateArguments(trxPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static InvocationPaths CreateInvocation()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-authorization-{Guid.NewGuid():N}");
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
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }

    private sealed record InvocationPaths(string[] Arguments, string TrxPath);

    private sealed class ResultDirectoryCleanup : IDisposable
    {
        private readonly string _trxPath;

        internal ResultDirectoryCleanup(string trxPath)
        {
            _trxPath = trxPath;
        }

        public void Dispose()
        {
            var directory = Path.GetDirectoryName(_trxPath);
            if (directory != null && System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}

internal sealed record AuthorizedChildResult(OwnedProcessOutcome Outcome, string TrxPath) : IDisposable
{
    public void Dispose()
    {
        var directory = Path.GetDirectoryName(TrxPath);
        if (directory != null && System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.Delete(directory, recursive: true);
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
