using System.Collections.ObjectModel;
using System.Diagnostics;
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
        ArgumentNullException.ThrowIfNull(options);
        ValidateConfiguration(options.Configuration);
        var project = new FileInfo(options.ProjectPath);
        if (!project.Exists)
        {
            throw new FileNotFoundException("The repository test project is missing.", project.FullName);
        }

        var platform = CentralTestPolicy.GetCurrentPlatform();
        if (!CentralTestPolicy.SupportsPlatform(project.FullName, platform))
        {
            var declared = CentralTestPolicy.ReadProjectPlatforms(project.FullName);
            throw new InvalidOperationException(
                $"Test project {project.FullName} supports [{string.Join(", ", declared)}] and cannot run on '{platform}'.");
        }

        var policy = CentralTestPolicy.ReadRunnerPolicy(
            options.RepositoryRoot,
            project.FullName);
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            throw new InvalidOperationException(
                $"The xUnit in-process runner requires class locators instead of a VSTest filter: {project.FullName}");
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
                assemblyPath);
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
        try
        {
            if (File.Exists(validationTrxPath))
            {
                File.Delete(validationTrxPath);
            }

            var budget = TransitionBudget.Start(
                TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
                CleanupGrace);
            var authorization = CentralTestAuthorization.Issue(
                arguments,
                options.RepositoryRoot);
            await using var authorizationScope = authorization.ConfigureAwait(false);
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
                await authorization.CompleteAsync(budget, cancellationToken).ConfigureAwait(false);
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

                return new CentralTestRunResult(
                    outcome.ExitCode,
                    policy.Runner,
                    reportedTrxPath,
                    report,
                    outcome.Ownership);
            }
            finally
            {
                if (lease != null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (temporaryResultsDirectory != null && Directory.Exists(temporaryResultsDirectory))
            {
                Directory.Delete(temporaryResultsDirectory, recursive: true);
            }
        }
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
                $"No test projects were found under {testsRoot}.");
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
            Console.WriteLine($"Testing {project}");
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
                throw new InvalidOperationException($"Test project failed: {project}");
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
        var arguments = new List<string>
        {
            "build",
            options.ProjectPath,
            "-c",
            options.Configuration
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
        var outcome = await lease.WaitAsync(cancellationToken).ConfigureAwait(false);
        WriteCapturedOutput(outcome.StandardOutput, outcome.StandardError);
        if (outcome.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The repository test project build failed with exit code {outcome.ExitCode}.");
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
