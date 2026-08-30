using System.Diagnostics;
using DownKyi.CentralTestRunner;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerOwnershipTests
{
    private const string BlockingModeVariable = "DOWNKYI_STAGE5_BLOCKING_MODE";
    private const string ReadyPathVariable = "DOWNKYI_STAGE5_READY_PATH";
    private const string TargetStreamMutationVariable =
        "DOWNKYI_TEST_MUTATE_CENTRAL_TARGET_STREAM_FORWARDING";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ProjectPath = Path.Combine(
        RepositoryRoot,
        "tests",
        "DownKyi.Architecture.Tests",
        "DownKyi.Architecture.Tests.csproj");

    [Fact]
    public async Task NormalTestChildCompletesUnderOwnedProcessLease()
    {
        var paths = CreateResultPaths();
        await File.WriteAllTextAsync(
                paths.TrxPath,
                "previous report must be replaced only on success",
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        try
        {
            var result = await CentralTestOrchestrator.RunProjectAsync(
                    CreateOptions(paths, executionTimeoutSeconds: 20),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.Ownership.OwnershipEstablished);
            Assert.Equal(1, result.Report.PassedExpectedClasses);
            Assert.Equal(paths.TrxPath, result.Report.ReportPath);
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    [Fact]
    public void NestedConditionalPlatformDeclarationIsRejected()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-platform-condition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Conditional.Tests.csproj");
        try
        {
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Choose>
                    <When Condition="'$(OS)' != ''">
                      <PropertyGroup>
                        <DownKyiTestPlatforms>Windows;Linux;macOS</DownKyiTestPlatforms>
                      </PropertyGroup>
                    </When>
                  </Choose>
                </Project>
                """);

            var failure = Assert.Throws<InvalidOperationException>(
                () => CentralTestPolicy.ReadProjectPlatforms(projectPath));

            Assert.Contains("unconditional", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteResultDirectory(directory);
        }
    }

    [Fact]
    public void ProjectBuildCannotRetainMsBuildOrSharedCompilerServers()
    {
        var options = new CentralTestProjectOptions(
            RepositoryRoot,
            ProjectPath,
            "Release",
            noRestore: true,
            noBuild: false,
            resultsDirectory: null,
            trxName: null,
            classNames: null,
            filter: null,
            executionTimeoutSeconds: 20);

        var arguments = CentralTestOrchestrator.CreateBuildArgumentsForTesting(options);

        Assert.Contains("-nodeReuse:false", arguments);
        Assert.Contains("-p:UseSharedCompilation=false", arguments);
        Assert.DoesNotContain("build-server", arguments);
    }

    [Theory]
    [InlineData("../outside.trx")]
    [InlineData("..\\outside.trx")]
    [InlineData("nested/outside.trx")]
    [InlineData("nested\\outside.trx")]
    [InlineData("/outside.trx")]
    [InlineData("C:\\outside.trx")]
    [InlineData("C:outside.trx")]
    [InlineData(".")]
    [InlineData("..")]
    public void TrxPublicationNameCannotEscapeItsResultsDirectory(string trxName)
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            new CentralTestProjectOptions(
                RepositoryRoot,
                ProjectPath,
                "Release",
                noRestore: true,
                noBuild: true,
                resultsDirectory: Path.GetTempPath(),
                trxName,
                classNames: null,
                filter: null,
                executionTimeoutSeconds: 20));

        Assert.Equal("trxName", failure.ParamName);
        Assert.Contains("file name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelledProjectBuildPreservesOwnedProcessOutput()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-build-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Blocked.proj");
        var readyPath = Path.Combine(directory, "build-ready.json");
        var processSupervisionAssembly = typeof(OwnedProcessLease).Assembly.Location;
        await File.WriteAllTextAsync(
                projectPath,
                $"""
                <Project>
                  <Target Name="Build">
                    <Message Text="stage5-owned-build-output" Importance="High" />
                    <Exec Command="dotnet &quot;{processSupervisionAssembly}&quot; {SupervisorHost.CollectorBlockWithReadyArgument} &quot;{readyPath}&quot;" />
                  </Target>
                </Project>
                """,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        var options = new CentralTestProjectOptions(
            directory,
            projectPath,
            "Release",
            noRestore: true,
            noBuild: false,
            resultsDirectory: null,
            trxName: null,
            classNames: null,
            filter: null,
            executionTimeoutSeconds: 30);
        using var output = new ScopedConsoleOutput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            var ready = WaitForReadyPathAsync(readyPath, cancellation.Token);
            var build = CentralTestOrchestrator.BuildProjectForTestingAsync(
                options,
                cancellation.Token);
            var first = await Task.WhenAny(ready, build).ConfigureAwait(true);
            Assert.Same(ready, first);
            await ready.ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(() => build)
                .ConfigureAwait(true);

            Assert.Equal(OwnedProcessFailureKind.CallerCancelled, failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            Assert.Contains(
                "stage5-owned-build-output",
                output.StandardOutput,
                StringComparison.Ordinal);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            DeleteResultDirectory(directory);
        }
    }

    [Fact]
    public async Task FocusedTemporaryRunReturnsAUsableValidatedReport()
    {
        var options = new CentralTestProjectOptions(
            RepositoryRoot,
            ProjectPath,
            "Release",
            noRestore: true,
            noBuild: true,
            resultsDirectory: null,
            trxName: null,
            [typeof(CentralTestRunnerBlockingFixture).FullName!],
            filter: null,
            executionTimeoutSeconds: 20);

        var result = await CentralTestOrchestrator.RunProjectAsync(
                options,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.TrxPath);
        Assert.Null(result.Report.ReportPath);
        Assert.Equal(1, result.Report.ExecutedExpectedClasses);
        Assert.Equal(1, result.Report.PassedExpectedClasses);
        Assert.Equal(
            result.Report,
            CentralTestExecutionValidator.ValidateExpectedExecutionReport(
                result.ExitCode,
                result.Report,
                [typeof(CentralTestRunnerBlockingFixture).FullName!]));
    }

    [Fact]
    public async Task FailedRunPreservesThePreviouslyCommittedTrx()
    {
        var paths = CreateResultPaths();
        const string previousReport = "previous authoritative report";
        await File.WriteAllTextAsync(
                paths.TrxPath,
                previousReport,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => CentralTestOrchestrator.RunProjectForTestingAsync(
                        CreateOptions(paths, executionTimeoutSeconds: 20),
                        CentralTestRunnerMutation.FailAuthorizationBeforeCompletion,
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.Contains(
                "Injected central test authorization failure.",
                failure.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                previousReport,
                await File.ReadAllTextAsync(
                        paths.TrxPath,
                        TestContext.Current.CancellationToken)
                    .ConfigureAwait(true));
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    [Fact]
    public async Task NormalAndExceptionalProjectDiagnosticsDoNotExposeCheckoutPaths()
    {
        using var output = new ScopedConsoleOutput();
        CentralTestOrchestrator.WriteProjectStart(RepositoryRoot, ProjectPath);
        Assert.Contains(
            "Testing tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj",
            output.StandardOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(RepositoryRoot, output.StandardOutput, StringComparison.OrdinalIgnoreCase);

        var siblingProject = Path.Combine(
            Directory.GetParent(RepositoryRoot)!.FullName,
            "sibling-checkout",
            "Missing.Tests.csproj");
        var options = new CentralTestProjectOptions(
            RepositoryRoot,
            siblingProject,
            "Release",
            noRestore: true,
            noBuild: true,
            resultsDirectory: null,
            trxName: null,
            classNames: null,
            filter: null,
            executionTimeoutSeconds: 20);

        var failure = await Assert.ThrowsAsync<FileNotFoundException>(
                () => CentralTestOrchestrator.RunProjectAsync(
                    options,
                    TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Contains("../sibling-checkout/Missing.Tests.csproj", failure.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain(RepositoryRoot, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RepositoryRoot, failure.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HungTestChildUsesTheLeaseDeadlineAndIsReaped()
    {
        var paths = CreateResultPaths();
        var readyPath = Path.Combine(paths.Directory, "hung.pid");
        using var mode = new ScopedEnvironmentVariable(BlockingModeVariable, "hang");
        using var ready = new ScopedEnvironmentVariable(ReadyPathVariable, readyPath);
        using var output = new ScopedConsoleOutput();
        try
        {
            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => CentralTestOrchestrator.RunProjectAsync(
                        CreateOptions(paths, executionTimeoutSeconds: 5),
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.Equal(OwnedProcessFailureKind.OperationDeadlineExceeded, failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            AssertProcessExited(await ReadReadyProcessIdAsync(readyPath).ConfigureAwait(true));
            Assert.Contains("stage5-owned-test-stdout", output.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("stage5-owned-test-stderr", output.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    [Fact]
    public async Task RunnerCancellationIsTypedAndReapsTheTestChild()
    {
        var paths = CreateResultPaths();
        var readyPath = Path.Combine(paths.Directory, "cancel.pid");
        using var mode = new ScopedEnvironmentVariable(BlockingModeVariable, "hang");
        using var ready = new ScopedEnvironmentVariable(ReadyPathVariable, readyPath);
        using var output = new ScopedConsoleOutput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        try
        {
            var run = CentralTestOrchestrator.RunProjectAsync(
                CreateOptions(paths, executionTimeoutSeconds: 30),
                cancellation.Token);
            var processId = await ReadReadyProcessIdAsync(readyPath).ConfigureAwait(true);
            await cancellation.CancelAsync().ConfigureAwait(true);

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(() => run)
                .ConfigureAwait(true);

            Assert.Equal(OwnedProcessFailureKind.CallerCancelled, failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
            AssertProcessExited(processId);
            Assert.Contains(
                "stage5-owned-test-stdout",
                failure.Failure.StandardOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "stage5-owned-test-stderr",
                failure.Failure.StandardError,
                StringComparison.Ordinal);
            Assert.Contains("stage5-owned-test-stdout", output.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("stage5-owned-test-stderr", output.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    [Fact]
    public async Task SupervisorOwnershipFailureCannotReachTestCode()
    {
        var paths = CreateResultPaths();
        var arguments = CreateFixtureArguments(paths.TrxPath);
        var authorization = CentralTestAuthorization.IssueForTesting(
            arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BlockingModeVariable] = "ready-only",
            [ReadyPathVariable] = Path.Combine(paths.Directory, "must-not-run.pid")
        };
        authorization.ApplyEnvironment(environment);
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                    () => OwnedProcessLease.StartForTestingAsync(
                        new LaunchSpec("dotnet", arguments, RepositoryRoot, environment, true),
                        budget,
                        ProcessOwnershipMutation.FailOwnershipEstablishment,
                        TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.False(File.Exists(environment[ReadyPathVariable]));
            Assert.False(File.Exists(paths.TrxPath));
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    [Fact]
    public async Task OwnerLifetimeEofTerminatesAndReapsAuthorizedTestChild()
    {
        var paths = CreateResultPaths();
        var readyPath = Path.Combine(paths.Directory, "owner-eof.pid");
        var arguments = CreateFixtureArguments(paths.TrxPath);
        var authorization = CentralTestAuthorization.IssueForTesting(
            arguments,
            RepositoryRoot,
            CentralTestAuthorizationMutation.None);
        await using var authorizationScope = authorization.ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BlockingModeVariable] = "hang",
            [ReadyPathVariable] = readyPath
        };
        authorization.ApplyEnvironment(environment);
        var processMutation = string.Equals(
            Environment.GetEnvironmentVariable(TargetStreamMutationVariable),
            "1",
            StringComparison.Ordinal)
            ? ProcessOwnershipMutation.SkipTargetStreamForwarding
            : ProcessOwnershipMutation.None;
        var budget = TransitionBudget.Start(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));
        var lease = await OwnedProcessLease.StartForTestingAsync(
                new LaunchSpec("dotnet", arguments, RepositoryRoot, environment, true),
                budget,
                processMutation,
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
            var processId = await ReadReadyProcessIdAsync(readyPath).ConfigureAwait(true);
            lease.CloseOwnerLifetimeForTesting();

            var failure = await Assert.ThrowsAsync<OwnedProcessExecutionException>(
                    () => lease.WaitAsync(TestContext.Current.CancellationToken))
                .ConfigureAwait(true);

            Assert.Equal(OwnedProcessFailureKind.ExecutionFailed, failure.Failure.Kind);
            Assert.Empty(failure.CleanupFailures);
            AssertProcessExited(processId);
            Assert.Contains(
                "stage5-owned-test-stdout",
                failure.Failure.StandardOutput,
                StringComparison.Ordinal);
            Assert.Contains(
                "stage5-owned-test-stderr",
                failure.Failure.StandardError,
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteResultDirectory(paths.Directory);
        }
    }

    private static CentralTestProjectOptions CreateOptions(
        ResultPaths paths,
        int executionTimeoutSeconds)
    {
        return new CentralTestProjectOptions(
            RepositoryRoot,
            ProjectPath,
            "Release",
            noRestore: true,
            noBuild: true,
            paths.Directory,
            Path.GetFileName(paths.TrxPath),
            [typeof(CentralTestRunnerBlockingFixture).FullName!],
            filter: null,
            executionTimeoutSeconds);
    }

    private static string[] CreateFixtureArguments(string trxPath) =>
    [
        typeof(CentralTestRunnerOwnershipTests).Assembly.Location,
        "-noLogo",
        "-noColor",
        "-noAutoReporters",
        "-reporter",
        "quiet",
        "-parallel",
        "none",
        "-class",
        typeof(CentralTestRunnerBlockingFixture).FullName!,
        "-trx",
        trxPath
    ];

    private static ResultPaths CreateResultPaths()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-stage5-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return new ResultPaths(directory, Path.Combine(directory, "ownership.trx"));
    }

    private static async Task<int> ReadReadyProcessIdAsync(string readyPath)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(readyPath))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(true);
        }

        while (true)
        {
            try
            {
                return int.Parse(
                    await File.ReadAllTextAsync(readyPath, timeout.Token).ConfigureAwait(true),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token).ConfigureAwait(true);
            }
        }
    }

    private static async Task WaitForReadyPathAsync(
        string readyPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(readyPath)
            ?? throw new InvalidOperationException("The build fixture directory is unavailable.");
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

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Test child process {processId} is still running.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static void DeleteResultDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    private sealed record ResultPaths(string Directory, string TrxPath);

    private sealed class ScopedConsoleOutput : IDisposable
    {
        private readonly TextWriter _originalOutput;
        private readonly TextWriter _originalError;
        private readonly StringWriter _standardOutput = new();
        private readonly StringWriter _standardError = new();

        internal ScopedConsoleOutput()
        {
            _originalOutput = Console.Out;
            _originalError = Console.Error;
            Console.SetOut(_standardOutput);
            Console.SetError(_standardError);
        }

        internal string StandardOutput => _standardOutput.ToString();

        internal string StandardError => _standardError.ToString();

        public void Dispose()
        {
            Console.SetOut(_originalOutput);
            Console.SetError(_originalError);
            _standardOutput.Dispose();
            _standardError.Dispose();
        }
    }

    private sealed class ScopedEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        internal ScopedEnvironmentVariable(string name, string value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}

public sealed class CentralTestRunnerBlockingFixture
{
    [Fact]
    public async Task TestCodeRunsOnlyAfterOwnershipAndCanBeBoundedlyStopped()
    {
        var blocking = string.Equals(
            Environment.GetEnvironmentVariable("DOWNKYI_STAGE5_BLOCKING_MODE"),
            "hang",
            StringComparison.Ordinal);
        if (blocking)
        {
            await Console.Out.WriteLineAsync("stage5-owned-test-stdout").ConfigureAwait(true);
            await Console.Error.WriteLineAsync("stage5-owned-test-stderr").ConfigureAwait(true);
            await Console.Out.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await Console.Error.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var readyPath = Environment.GetEnvironmentVariable("DOWNKYI_STAGE5_READY_PATH");
        if (!string.IsNullOrWhiteSpace(readyPath))
        {
            await PublishReadyProcessIdAsync(readyPath).ConfigureAwait(true);
        }

        if (blocking)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }

        Assert.True(Environment.ProcessId > 0);
    }

    private static async Task PublishReadyProcessIdAsync(string readyPath)
    {
        var temporaryPath = $"{readyPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(payload, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                await stream.FlushAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            File.Move(temporaryPath, readyPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
