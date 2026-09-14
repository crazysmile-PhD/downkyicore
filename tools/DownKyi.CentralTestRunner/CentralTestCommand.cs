namespace DownKyi.CentralTestRunner;

internal static class CentralTestCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Expected run-project or run-solution.", nameof(args));
        }

        var options = CommandOptions.Parse(args[1..]);
        return args[0] switch
        {
            "run-project" => await RunProjectAsync(options, cancellationToken).ConfigureAwait(false),
            "run-solution" => await RunSolutionAsync(options, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command: {args[0]}", nameof(args))
        };
    }

    private static async Task<int> RunSolutionAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
        var projects = TestProjectCatalog.DiscoverProjects(repositoryRoot);
        if (projects.Length == 0)
        {
            throw new InvalidDataException("No runnable test projects were discovered.");
        }

        var platform = TestProjectCatalog.GetCurrentPlatform();
        var selected = projects
            .Where(project => project.Platforms.Contains(platform, StringComparer.Ordinal))
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidDataException($"No test projects support the current platform '{platform}'.");
        }

        foreach (var project in selected)
        {
            var trxOutput = TrxResultStore.ResolveOutput(
                repositoryRoot,
                options.ResultsDirectory,
                $"{Path.GetFileNameWithoutExtension(project.Project)}.trx",
                project.Project);
            TrxResultStore.ClearStale(trxOutput);
        }

        Console.WriteLine($"Selected {selected.Length} of {projects.Length} test projects for '{platform}'.");
        foreach (var project in selected)
        {
            var projectOptions = options with
            {
                Project = project.Project,
                TrxName = $"{Path.GetFileNameWithoutExtension(project.Project)}.trx",
                Classes = []
            };
            var exitCode = await RunProjectAsync(projectOptions, cancellationToken, projects).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return exitCode;
            }
        }

        Console.WriteLine($"Passed {selected.Length} '{platform}' test projects.");
        return 0;
    }

    private static async Task<int> RunProjectAsync(
        CommandOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<TestProjectDefinition>? discoveredProjects = null)
    {
        if (string.IsNullOrWhiteSpace(options.Project))
        {
            throw new ArgumentException("run-project requires --project.");
        }

        var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
        var relativeProject = TestProjectCatalog.NormalizeProject(repositoryRoot, options.Project);
        var definition = (discoveredProjects ?? TestProjectCatalog.DiscoverProjects(repositoryRoot)).SingleOrDefault(project =>
            string.Equals(project.Project, relativeProject, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new InvalidOperationException($"Test project is not allowlisted: {relativeProject}");
        }

        var platform = TestProjectCatalog.GetCurrentPlatform();
        if (!definition.Platforms.Contains(platform, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Test project {relativeProject} supports [{string.Join(", ", definition.Platforms)}] and cannot run on '{platform}'.");
        }

        var projectPath = Path.GetFullPath(relativeProject, repositoryRoot);
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Allowlisted test project is missing.", relativeProject);
        }

        var trxOutput = TrxResultStore.ResolveOutput(
            repositoryRoot,
            options.ResultsDirectory,
            options.TrxName,
            relativeProject);
        TrxResultStore.ClearStale(trxOutput);
        var resultsDirectory = trxOutput.ResultsDirectory;
        var trxName = trxOutput.TrxName;
        var trxPath = trxOutput.TrxPath;

        if (!options.NoBuild)
        {
            var buildExitCode = await BuildProcessRunner.BuildProjectAsync(
                projectPath,
                options.Configuration,
                options.NoRestore,
                cancellationToken).ConfigureAwait(false);
            if (buildExitCode != 0)
            {
                return buildExitCode;
            }
        }

        var startInfo = definition.UseInProcessXunit
            ? TestInvocationFactory.CreateInProcessXunitStartInfo(
                projectPath,
                definition.InProcessTargetFramework!,
                options,
                trxPath)
            : TestInvocationFactory.CreateVstestStartInfo(
                projectPath,
                options,
                resultsDirectory,
                trxName);
        startInfo.WorkingDirectory = repositoryRoot;
        var evidenceDirectory = string.IsNullOrWhiteSpace(options.EvidenceDirectory)
            ? Path.Combine(repositoryRoot, "artifacts", "test-flight-recorder")
            : Path.GetFullPath(options.EvidenceDirectory, repositoryRoot);
        var testIdentity = options.Classes.Length > 0
            ? string.Join(",", options.Classes.Order(StringComparer.Ordinal))
            : string.IsNullOrWhiteSpace(options.Filter) ? "all" : options.Filter;

        var result = await FlightRecorderExecution.RunAsync(
            new ProcessExecutionRequest(
                relativeProject,
                testIdentity,
                startInfo,
                TimeSpan.FromSeconds(options.TimeoutSeconds),
                TimeSpan.FromSeconds(5),
                evidenceDirectory),
            cancellationToken).ConfigureAwait(false);
        // Presentation follows owner completion, so a blocked console cannot
        // hold an owned pipe reader or postpone mandatory process termination.
        if (result.ExitCode == 0 &&
            result.LifecycleOutcome?.Trigger == ProcessLifecycleTrigger.RootExited)
        {
            try
            {
                if (result.Recorder.StdoutTail.Length > 0)
                {
                    await Console.Out.WriteLineAsync(result.Recorder.StdoutTail).ConfigureAwait(false);
                }
                if (result.Recorder.StderrTail.Length > 0)
                {
                    await Console.Error.WriteLineAsync(result.Recorder.StderrTail).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Presentation follows cleanup and cannot change its outcome.
            }
        }
        if (result.ExitCode != 0)
        {
            var evidenceIdentity = Path.GetRelativePath(repositoryRoot, result.EvidencePath)
                .Replace('\\', '/');
            if (result.EvidenceWriteFailed)
            {
                var lifecycle = result.LifecycleOutcome;
                var redactor = result.Recorder.Redactor;
                await Program.WriteDiagnosticBestEffortAsync(
                    $"Test flight recorder incomplete: {evidenceIdentity}; " +
                    $"primary={redactor.Redact(lifecycle?.PrimaryFailure?.Message ?? "unknown")}; " +
                    $"live={redactor.Redact(lifecycle?.LiveEvidence ?? "unavailable")}; " +
                    $"secondary={redactor.Redact(lifecycle?.SecondaryCleanupFailure?.Message ?? "none")}")
                    .ConfigureAwait(false);
            }
            else
            {
                await Program.WriteDiagnosticBestEffortAsync(
                    $"Test flight recorder preserved: {evidenceIdentity}").ConfigureAwait(false);
            }
            return result.ExitCode;
        }

        try
        {
            TrxResultStore.Validate(trxPath, $"{relativeProject}:{trxName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            await FlightRecorderExecution.PreservePostExitFailureAsync(
                result,
                "trx_validation_failed",
                exception.Message).ConfigureAwait(false);
            var evidenceIdentity = Path.GetRelativePath(repositoryRoot, result.EvidencePath)
                .Replace('\\', '/');
            await Program.WriteDiagnosticBestEffortAsync(
                $"Test flight recorder preserved: {evidenceIdentity}").ConfigureAwait(false);
            return 1;
        }

        await FlightRecorderExecution.DiscardAsync(result).ConfigureAwait(false);
        return 0;
    }

}
