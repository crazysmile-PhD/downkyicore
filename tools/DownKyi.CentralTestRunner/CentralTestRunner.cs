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
            if (File.Exists(validationTrxPath))
            {
                File.Delete(validationTrxPath);
            }

            var budget = TransitionBudget.Start(
                TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
                CleanupGrace);
            CentralTestAuthorization? authorization = CentralTestAuthorization.Issue(
                arguments,
                options.RepositoryRoot);
            var environment = new Dictionary<string, string?>(
                options.EnvironmentVariables,
                StringComparer.Ordinal)
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
                var report = CentralTestExecutionValidator.ValidateReport(
                    validationTrxPath,
                    options.ClassNames);
                if (outcome.ExitCode == 0 && report.Failed > 0)
                {
                    throw new InvalidDataException(
                        "A successful runner report cannot contain failed test results.");
                }

                result = new CentralTestRunResult(
                    outcome.ExitCode,
                    policy.Runner,
                    reportedTrxPath,
                    report,
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
        finally
        {
            if (temporaryResultsDirectory != null && Directory.Exists(temporaryResultsDirectory))
            {
                try
                {
                    Directory.Delete(temporaryResultsDirectory, recursive: true);
                }
                catch (Exception cleanupFailure)
                {
                    cleanupFailures.Add(cleanupFailure);
                }
            }
            if (temporaryResultsDirectory != null &&
                mutation.HasFlag(CentralTestRunnerMutation.FailTemporaryResultsCleanup))
            {
                cleanupFailures.Add(new IOException(
                    "Injected temporary TRX directory cleanup failure."));
            }
        }

        ThrowPreservingPrimaryFailure(primaryFailure, cleanupFailures);
        return result
            ?? throw new InvalidOperationException("Central test execution produced no result.");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Every bounded project outcome is retained so sibling evidence can complete before the shard fails.")]
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
        var selected = SelectSolutionProjects(
            allProjects,
            platform,
            options.ShardIndex,
            options.ShardCount);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException(
                $"Test shard {options.ShardIndex} of {options.ShardCount} owns no '{platform}' projects.");
        }

        Console.WriteLine(
            $"Selected {selected.Count} of {allProjects.Length} test projects for '{platform}' " +
            $"shard {options.ShardIndex} of {options.ShardCount}.");
        if (!options.NoBuild)
        {
            foreach (var project in selected)
            {
                await BuildProjectAsync(
                        CreateSolutionProjectOptions(options, project, noBuild: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        using var concurrency = new SemaphoreSlim(options.MaxParallelProjects);
        var executions = selected.Select(async (project, index) =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WriteProjectStart(options.RepositoryRoot, project);
                var result = await RunProjectAsync(
                        CreateSolutionProjectOptions(options, project, noBuild: true),
                        cancellationToken)
                    .ConfigureAwait(false);
                return (Index: index, Project: project, Result: result, Failure: (Exception?)null);
            }
            catch (Exception failure)
            {
                return (Index: index, Project: project, Result: (CentralTestRunResult?)null, Failure: failure);
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();
        var outcomes = await Task.WhenAll(executions).ConfigureAwait(false);
        var failures = outcomes
            .Where(outcome => outcome.Failure != null)
            .OrderBy(outcome => outcome.Index)
            .Select(outcome => new InvalidOperationException(
                $"Test project failed: {FormatRepositoryPath(options.RepositoryRoot, outcome.Project)}",
                outcome.Failure))
            .ToList();
        failures.AddRange(outcomes
            .Where(outcome => outcome.Failure == null && outcome.Result?.ExitCode != 0)
            .OrderBy(outcome => outcome.Index)
            .Select(outcome => new InvalidOperationException(
                $"Test project failed: {FormatRepositoryPath(options.RepositoryRoot, outcome.Project)}")));
        if (failures.Count > 0)
        {
            throw new AggregateException("Repository test shard failed.", failures);
        }

        var results = outcomes
            .OrderBy(outcome => outcome.Index)
            .Select(outcome => new CentralTestProjectRunResult(
                FormatRepositoryPath(options.RepositoryRoot, outcome.Project),
                outcome.Result!))
            .ToArray();
        Console.WriteLine($"Passed {results.Length} '{platform}' test projects.");
        return new CentralTestSolutionResult(
            platform,
            selected.Count,
            options.ShardIndex,
            options.ShardCount,
            new ReadOnlyCollection<CentralTestProjectRunResult>(results));
    }

    internal static IReadOnlyList<string> SelectSolutionProjectsForTesting(
        IEnumerable<string> projectPaths,
        string platform,
        int shardIndex,
        int shardCount) =>
        SelectSolutionProjects(projectPaths, platform, shardIndex, shardCount);

    private static ReadOnlyCollection<string> SelectSolutionProjects(
        IEnumerable<string> projectPaths,
        string platform,
        int shardIndex,
        int shardCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shardIndex, shardCount);
        var owned = CentralTestPolicy.SelectProjects(projectPaths, platform);
        return new ReadOnlyCollection<string>(owned
            .Where((_, index) => index % shardCount == shardIndex)
            .ToArray());
    }

    private static CentralTestProjectOptions CreateSolutionProjectOptions(
        CentralTestSolutionOptions options,
        string project,
        bool noBuild) =>
        new(
            options.RepositoryRoot,
            project,
            options.Configuration,
            options.NoRestore || noBuild,
            noBuild,
            options.ResultsDirectory,
            $"{Path.GetFileNameWithoutExtension(project)}.trx",
            classNames: null,
            filter: null,
            options.ExecutionTimeoutSeconds);

    private static async Task BuildProjectAsync(
        CentralTestProjectOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "build",
            options.ProjectPath,
            "-c",
            options.Configuration,
            "-nodeReuse:false",
            "-p:TreatWarningsAsErrors=true",
            "-p:CodeAnalysisTreatWarningsAsErrors=true",
            "-p:EnableNETAnalyzers=true",
            "-p:AnalysisMode=All",
            "-p:EnforceCodeStyleInBuild=true",
            "-p:UseSharedCompilation=false"
        };
        if (options.NoRestore)
        {
            arguments.Add("--no-restore");
        }

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
        try
        {
            var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
            WriteCapturedOutput(outcome.StandardOutput, outcome.StandardError);
            if (outcome.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The repository test project build failed with exit code {outcome.ExitCode}.");
            }
        }
        catch (OwnedProcessExecutionException failure)
        {
            WriteCapturedOutput(
                failure.Failure.StandardOutput,
                failure.Failure.StandardError);
            throw;
        }
    }

    private static ReadOnlyCollection<string> CreateCanonicalArguments(
        string assemblyPath,
        CentralTestProjectPolicy policy,
        IReadOnlyList<string> classNames,
        out string? temporaryResultsDirectory,
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

        temporaryResultsDirectory = null;
        reportedTrxPath = null;
        var validationDirectory = resultsDirectory;
        if (validationDirectory == null)
        {
            temporaryResultsDirectory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-test-{Guid.NewGuid():N}");
            validationDirectory = temporaryResultsDirectory;
        }
        else
        {
            reportedTrxPath = Path.Combine(
                validationDirectory,
                string.IsNullOrWhiteSpace(trxName)
                    ? $"{Path.GetFileNameWithoutExtension(assemblyPath)}.trx"
                    : trxName);
        }

        Directory.CreateDirectory(validationDirectory);
        validationTrxPath = reportedTrxPath ?? Path.Combine(
            validationDirectory,
            $"{Path.GetFileNameWithoutExtension(assemblyPath)}.trx");
        arguments.Add("-trx");
        arguments.Add(validationTrxPath);
        return new ReadOnlyCollection<string>(arguments);
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
