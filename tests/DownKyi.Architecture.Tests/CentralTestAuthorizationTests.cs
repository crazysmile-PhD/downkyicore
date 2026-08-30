using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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

    [Fact]
    public async Task CallerCancellationPreservesTheOriginalAuthorizationToken()
    {
        var paths = CreateInvocation();
        using var resultCleanup = new ResultDirectoryCleanup(paths.TrxPath);
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync().ConfigureAwait(true);

        var failure = await Assert.ThrowsAsync<OperationCanceledException>(
                () => authorization.CompleteAsync(
                    budget,
                    CancellationToken.None,
                    cancellation.Token))
            .ConfigureAwait(true);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

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
        var resultDirectory = Path.GetDirectoryName(paths.TrxPath)
            ?? throw new InvalidOperationException("The authorization result directory is unavailable.");
        var readyPath = Path.Combine(resultDirectory, "target-ready.json");
        var exitSignalPath = Path.Combine(resultDirectory, "target-exit.signal");
        var authorization = CentralTestAuthorization.IssueForTesting(
            paths.Arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var processSupervisionAssembly = typeof(OwnedProcessLease).Assembly.Location;
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec(
                    "dotnet",
                    [
                        processSupervisionAssembly,
                        SupervisorHost.ExitOnFileSignalWithReadyArgument,
                        readyPath,
                        exitSignalPath
                    ],
                    Path.GetDirectoryName(processSupervisionAssembly)
                        ?? throw new InvalidOperationException(
                            "The target-exit fixture directory is unavailable."),
                    environment,
                    closeStandardInput: true),
                budget,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await using var leaseScope = lease.ConfigureAwait(false);
        await WaitForReadyPathAsync(readyPath, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        using (var ready = JsonDocument.Parse(
                   await File.ReadAllTextAsync(
                           readyPath,
                           TestContext.Current.CancellationToken)
                       .ConfigureAwait(true)))
        {
            Assert.True(ready.RootElement.GetProperty("WatcherArmed").GetBoolean());
        }
        var authorizationCompletion = authorization.CompleteAsync(
            budget,
            lease.TargetExitedToken,
            TestContext.Current.CancellationToken);
        Assert.False(authorizationCompletion.IsCompleted);
        var remainingBeforeExitSignal = budget.RemainingOperation;
        Assert.True(
            remainingBeforeExitSignal > TimeSpan.Zero,
            "The operation budget expired before the target-exit transition began.");
        var elapsedAfterExitSignal = Stopwatch.StartNew();
        await File.WriteAllTextAsync(
                exitSignalPath,
                "exit",
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => authorizationCompletion)
            .ConfigureAwait(true);
        elapsedAfterExitSignal.Stop();
        var outcome = await lease.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Contains("exited before authorization completed", failure.Message, StringComparison.Ordinal);
        Assert.True(
            elapsedAfterExitSignal.Elapsed < TimeSpan.FromSeconds(5),
            $"Authorization waited {elapsedAfterExitSignal.Elapsed} after the target exit signal.");
        var remainingAfterAuthorization = budget.RemainingOperation;
        Assert.True(
            remainingAfterAuthorization > TimeSpan.Zero,
            "Authorization consumed the caller's remaining operation budget after target exit; " +
            $"remaining before signal {remainingBeforeExitSignal}.");
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

    private static async Task WaitForReadyPathAsync(
        string readyPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(readyPath)
            ?? throw new InvalidOperationException("The target-ready directory is unavailable.");
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, Path.GetFileName(readyPath));
        FileSystemEventHandler created = (_, _) => completion.TrySetResult();
        RenamedEventHandler renamed = (_, _) => completion.TrySetResult();
        watcher.Created += created;
        watcher.Renamed += renamed;
        watcher.EnableRaisingEvents = true;
        if (File.Exists(readyPath))
        {
            completion.TrySetResult();
        }

        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
