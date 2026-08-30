using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using DownKyi.ProcessSupervision;

#pragma warning disable CA1515 // PowerShell compatibility wrappers invoke this compiled owner.

namespace DownKyi.CentralTestRunner;

public static class CentralTestOrchestrator
{
    private static readonly TimeSpan CleanupGrace = TimeSpan.FromSeconds(5);

    public static async Task<CentralTestRunResult> RunProjectAsync(
        CentralTestProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        return await RunProjectCoreAsync(
                options,
                CentralTestRunnerMutation.None,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<CentralTestRunResult> RunProjectForTestingAsync(
        CentralTestProjectOptions options,
        CentralTestRunnerMutation mutation,
        CancellationToken cancellationToken = default)
    {
        return await RunProjectCoreAsync(options, mutation, cancellationToken)
            .ConfigureAwait(false);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The orchestration boundary preserves the primary failure while collecting cleanup failures.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Authorization disposal is explicitly captured in the cleanup aggregation boundary.")]
    private static async Task<CentralTestRunResult> RunProjectCoreAsync(
        CentralTestProjectOptions options,
        CentralTestRunnerMutation mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateConfiguration(options.Configuration);
        var project = new FileInfo(options.ProjectPath);
        var displayProject = FormatRepositoryPath(options.RepositoryRoot, project.FullName);
        if (!project.Exists)
        {
            throw new FileNotFoundException("The repository test project is missing.", displayProject);
        }

        var platform = CentralTestPolicy.GetCurrentPlatform();
        var declared = CentralTestPolicy.ReadProjectPlatforms(project.FullName);
        if (!declared.Contains(platform, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Test project {displayProject} supports [{string.Join(", ", declared)}] and cannot run on '{platform}'.");
        }

        var policy = CentralTestPolicy.ReadRunnerPolicy(
            options.RepositoryRoot,
            project.FullName);
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            throw new InvalidOperationException(
                $"The xUnit in-process runner requires class locators instead of a VSTest filter: {displayProject}");
        }

        if (!options.NoBuild)
        {
            await BuildProjectAsync(options, cancellationToken).ConfigureAwait(false);
        }

        var projectDirectory = project.DirectoryName
            ?? throw new InvalidOperationException("The repository test project directory is unavailable.");
        var assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            options.Configuration,
            policy.TargetFramework,
            $"{Path.GetFileNameWithoutExtension(project.Name)}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException(
                "The xUnit in-process test assembly is missing.",
                FormatRepositoryPath(options.RepositoryRoot, assemblyPath));
        }

        var arguments = CreateCanonicalArguments(
            assemblyPath,
            policy,
            options.ClassNames,
            out var temporaryResultsDirectory,
            options.ResultsDirectory,
            options.TrxName,
            out var validationTrxPath,
            out var reportedTrxPath);
        Exception? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        CentralTestRunResult? result = null;
        try
        {
            var budget = TransitionBudget.Start(
                TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
                CleanupGrace);
            CentralTestAuthorization? authorization = CentralTestAuthorization.Issue(
                arguments,
                options.RepositoryRoot);
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DOWNKYI_LIFECYCLE_MARKER"] = null
            };
            authorization.ApplyEnvironment(environment);
            var launchSpec = new LaunchSpec(
                "dotnet",
                arguments,
                options.RepositoryRoot,
                environment,
                closeStandardInput: true);
            OwnedProcessLease? lease = null;
            try
            {
                lease = await OwnedProcessLease.StartAsync(
                        launchSpec,
                        budget,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (mutation.HasFlag(CentralTestRunnerMutation.FailAuthorizationBeforeCompletion))
                {
                    throw new InvalidOperationException(
                        "Injected central test authorization failure.");
                }
                await authorization.CompleteAsync(
                        budget,
                        lease.TargetExitedToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (mutation.HasFlag(CentralTestRunnerMutation.FailExecutionAfterAuthorization))
                {
                    throw new InvalidDataException(
                        "Injected central test execution failure.");
                }
                var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
                lease = null;

                WriteCapturedOutput(outcome.StandardOutput, outcome.StandardError);
                var validatedReport = CentralTestExecutionValidator.ValidateReport(
                    validationTrxPath,
                    options.ClassNames);
                if (outcome.ExitCode == 0 && validatedReport.Failed > 0)
                {
                    throw new InvalidDataException(
                        "A successful runner report cannot contain failed test results.");
                }
                result = new CentralTestRunResult(
                    outcome.ExitCode,
                    policy.Runner,
                    reportedTrxPath,
                    validatedReport,
                    outcome.Ownership);
            }
            catch (OwnedProcessExecutionException failure)
            {
                WriteCapturedOutput(
                    failure.Failure.StandardOutput,
                    failure.Failure.StandardError);
                throw;
            }
            finally
            {
                if (lease != null)
                {
                    await CaptureCleanupFailureAsync(
                            async () => await lease.DisposeAsync().ConfigureAwait(false),
                            cleanupFailures)
                        .ConfigureAwait(false);
                    if (mutation.HasFlag(CentralTestRunnerMutation.FailLeaseCleanup))
                    {
                        cleanupFailures.Add(new IOException(
                            "Injected owned process lease cleanup failure."));
                    }
                }
                if (authorization != null)
                {
                    await CaptureCleanupFailureAsync(
                            async () => await authorization.DisposeAsync().ConfigureAwait(false),
                            cleanupFailures)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
        }

        string? reportCandidatePath = null;
        if (primaryFailure == null && cleanupFailures.Count == 0 && result != null)
        {
            if (reportedTrxPath != null)
            {
                try
                {
                    reportCandidatePath = reportedTrxPath +
                        $".candidate-{Guid.NewGuid():N}";
                    File.Move(validationTrxPath, reportCandidatePath);
                }
                catch (Exception failure)
                {
                    primaryFailure = failure;
                }
            }

            if (primaryFailure == null)
            {
                await CaptureCleanupFailureAsync(
                        () => DeleteDirectoryIfPresentAsync(temporaryResultsDirectory),
                        cleanupFailures)
                    .ConfigureAwait(false);
                if (mutation.HasFlag(CentralTestRunnerMutation.FailTemporaryResultsCleanup))
                {
                    cleanupFailures.Add(new IOException(
                        "Injected temporary TRX directory cleanup failure."));
                }
            }

            if (primaryFailure == null && cleanupFailures.Count == 0)
            {
                try
                {
                    var report = result.Report with { ReportPath = reportedTrxPath };
                    if (reportedTrxPath != null)
                    {
                        File.Move(reportCandidatePath!, reportedTrxPath, overwrite: true);
                        reportCandidatePath = null;
                    }
                    result = result with { Report = report };
                }
                catch (Exception failure)
                {
                    primaryFailure = failure;
                }
            }
        }
        else
        {
            await CaptureCleanupFailureAsync(
                    () => DeleteDirectoryIfPresentAsync(temporaryResultsDirectory),
                    cleanupFailures)
                .ConfigureAwait(false);
            if (mutation.HasFlag(CentralTestRunnerMutation.FailTemporaryResultsCleanup))
            {
                cleanupFailures.Add(new IOException(
                    "Injected temporary TRX directory cleanup failure."));
            }
        }

        if (Directory.Exists(temporaryResultsDirectory))
        {
            await CaptureCleanupFailureAsync(
                    () => DeleteDirectoryIfPresentAsync(temporaryResultsDirectory),
                    cleanupFailures)
                .ConfigureAwait(false);
        }

        if (reportCandidatePath != null)
        {
            await CaptureCleanupFailureAsync(
                    () => DeleteFileIfPresentAsync(reportCandidatePath),
                    cleanupFailures)
                .ConfigureAwait(false);
        }

        ThrowPreservingPrimaryFailure(primaryFailure, cleanupFailures);
        return result
            ?? throw new InvalidOperationException("Central test execution produced no result.");
    }

    public static async Task<CentralTestSolutionResult> RunSolutionAsync(
        CentralTestSolutionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateConfiguration(options.Configuration);
        var testsRoot = Path.Combine(options.RepositoryRoot, "tests");
        var allProjects = Directory.GetFiles(
                testsRoot,
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (allProjects.Length == 0)
        {
            throw new InvalidOperationException(
                "No test projects were found under tests.");
        }

        var platform = CentralTestPolicy.GetCurrentPlatform();
        var selected = CentralTestPolicy.SelectProjects(allProjects, platform);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"No test projects are owned by '{platform}'.");
        }

        Console.WriteLine(
            $"Selected {selected.Count} of {allProjects.Length} test projects for '{platform}'.");
        var results = new List<CentralTestRunResult>();
        foreach (var project in selected)
        {
            var displayProject = FormatRepositoryPath(options.RepositoryRoot, project);
            WriteProjectStart(options.RepositoryRoot, project);
            var result = await RunProjectAsync(
                    new CentralTestProjectOptions(
                        options.RepositoryRoot,
                        project,
                        options.Configuration,
                        options.NoRestore,
                        options.NoBuild,
                        options.ResultsDirectory,
                        $"{Path.GetFileNameWithoutExtension(project)}.trx",
                        classNames: null,
                        filter: null,
                        options.ExecutionTimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
            results.Add(result);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Test project failed: {displayProject}");
            }
        }

        Console.WriteLine($"Passed {results.Count} '{platform}' test projects.");
        return new CentralTestSolutionResult(
            platform,
            selected.Count,
            new ReadOnlyCollection<CentralTestRunResult>(results));
    }

    private static async Task BuildProjectAsync(
        CentralTestProjectOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = CreateBuildArguments(options);

        var budget = TransitionBudget.Start(
            TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
            CleanupGrace);
        var lease = await OwnedProcessLease.StartAsync(
                new LaunchSpec(
                    "dotnet",
                    arguments,
                    options.RepositoryRoot,
                    environment: null,
                    closeStandardInput: true),
                budget,
                cancellationToken)
            .ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
        WriteCapturedOutput(outcome.StandardOutput, outcome.StandardError);
        if (outcome.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The repository test project build failed with exit code {outcome.ExitCode}.");
        }
    }

    internal static IReadOnlyList<string> CreateBuildArgumentsForTesting(
        CentralTestProjectOptions options) =>
        CreateBuildArguments(options);

    private static ReadOnlyCollection<string> CreateBuildArguments(
        CentralTestProjectOptions options)
    {
        var arguments = new List<string>
        {
            "build",
            options.ProjectPath,
            "-c",
            options.Configuration,
            "-nodeReuse:false",
            "-p:UseSharedCompilation=false"
        };
        if (options.NoRestore)
        {
            arguments.Add("--no-restore");
        }

        return new ReadOnlyCollection<string>(arguments);
    }

    private static ReadOnlyCollection<string> CreateCanonicalArguments(
        string assemblyPath,
        CentralTestProjectPolicy policy,
        IReadOnlyList<string> classNames,
        out string temporaryResultsDirectory,
        string? resultsDirectory,
        string? trxName,
        out string validationTrxPath,
        out string? reportedTrxPath)
    {
        var arguments = new List<string>
        {
            assemblyPath,
            "-noLogo",
            "-noColor",
            "-noAutoReporters",
            "-reporter",
            "quiet",
            "-parallel",
            policy.Parallel
        };
        foreach (var className in classNames)
        {
            arguments.Add("-class");
            arguments.Add(className);
        }

        var stagingName = $".downkyi-test-{Guid.NewGuid():N}";
        reportedTrxPath = null;
        if (resultsDirectory == null)
        {
            temporaryResultsDirectory = Path.Combine(
                Path.GetTempPath(),
                stagingName);
        }
        else
        {
            Directory.CreateDirectory(resultsDirectory);
            temporaryResultsDirectory = Path.Combine(resultsDirectory, stagingName);
            reportedTrxPath = Path.Combine(
                resultsDirectory,
                string.IsNullOrWhiteSpace(trxName)
                    ? $"{Path.GetFileNameWithoutExtension(assemblyPath)}.trx"
                    : trxName);
        }

        Directory.CreateDirectory(temporaryResultsDirectory);
        validationTrxPath = Path.Combine(
            temporaryResultsDirectory,
            $"{Path.GetFileNameWithoutExtension(assemblyPath)}.trx");
        arguments.Add("-trx");
        arguments.Add(validationTrxPath);
        return new ReadOnlyCollection<string>(arguments);
    }

    private static Task DeleteDirectoryIfPresentAsync(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static Task DeleteFileIfPresentAsync(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private static void WriteCapturedOutput(string standardOutput, string standardError)
    {
        if (!string.IsNullOrEmpty(standardOutput))
        {
            Console.Out.Write(standardOutput);
        }
        if (!string.IsNullOrEmpty(standardError))
        {
            Console.Error.Write(standardError);
        }
    }

    private static string FormatRepositoryPath(string repositoryRoot, string path)
    {
        return Path.GetRelativePath(
                Path.GetFullPath(repositoryRoot),
                Path.GetFullPath(path, repositoryRoot))
            .Replace('\\', '/');
    }

    internal static void WriteProjectStart(string repositoryRoot, string projectPath)
    {
        Console.WriteLine($"Testing {FormatRepositoryPath(repositoryRoot, projectPath)}");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Cleanup failures must be retained without replacing the primary execution failure.")]
    private static async Task CaptureCleanupFailureAsync(
        Func<Task> cleanup,
        List<Exception> cleanupFailures)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            cleanupFailures.Add(cleanupFailure);
        }
    }

    private static void ThrowPreservingPrimaryFailure(
        Exception? primaryFailure,
        List<Exception> cleanupFailures)
    {
        if (primaryFailure != null)
        {
            if (cleanupFailures.Count == 0)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            throw new AggregateException(
                "Central test execution failed and cleanup reported failure(s).",
                new[] { primaryFailure }.Concat(cleanupFailures));
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures.Single()).Throw();
        }
        if (cleanupFailures.Count > 1)
        {
            throw new AggregateException(
                "Central test cleanup encountered multiple failures.",
                cleanupFailures);
        }
    }

    private static void ValidateConfiguration(string configuration)
    {
        if (configuration is not ("Debug" or "Release"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration,
                "The central test configuration must be Debug or Release.");
        }
    }
}
