using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.CentralTestRunner;

internal sealed record TestProjectDefinition(
    string Project,
    string[] Platforms,
    string? InProcessTargetFramework)
{
    public bool UseInProcessXunit => InProcessTargetFramework is not null;
}

internal sealed record TestRunnerPolicyDocument(
    int SchemaVersion,
    TestRunnerPolicyProject[] Projects);

internal sealed record TestRunnerPolicyProject(
    string Project,
    string Runner,
    string? TargetFramework);

internal static class CentralTestCommand
{
    private static readonly JsonSerializerOptions PolicyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        var projects = DiscoverProjects(repositoryRoot);
        if (projects.Length == 0)
        {
            throw new InvalidDataException("No runnable test projects were discovered.");
        }

        var platform = GetCurrentPlatform();
        var selected = projects
            .Where(project => project.Platforms.Contains(platform, StringComparer.Ordinal))
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidDataException($"No test projects support the current platform '{platform}'.");
        }

        foreach (var project in selected)
        {
            var trxOutput = ResolveTrxOutput(
                repositoryRoot,
                options.ResultsDirectory,
                $"{Path.GetFileNameWithoutExtension(project.Project)}.trx",
                project.Project);
            ClearStaleTrx(trxOutput);
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
        var relativeProject = NormalizeProject(repositoryRoot, options.Project);
        var definition = (discoveredProjects ?? DiscoverProjects(repositoryRoot)).SingleOrDefault(project =>
            string.Equals(project.Project, relativeProject, StringComparison.Ordinal));
        if (definition is null)
        {
            throw new InvalidOperationException($"Test project is not allowlisted: {relativeProject}");
        }

        var platform = GetCurrentPlatform();
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

        var trxOutput = ResolveTrxOutput(
            repositoryRoot,
            options.ResultsDirectory,
            options.TrxName,
            relativeProject);
        ClearStaleTrx(trxOutput);
        var resultsDirectory = trxOutput.ResultsDirectory;
        var trxName = trxOutput.TrxName;
        var trxPath = trxOutput.TrxPath;

        if (!options.NoBuild)
        {
            var buildExitCode = await BuildProjectAsync(
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
            ? CreateInProcessXunitStartInfo(
                projectPath,
                definition.InProcessTargetFramework!,
                options,
                trxPath)
            : CreateVstestStartInfo(projectPath, options, resultsDirectory, trxName);
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
        if (result.ExitCode != 0)
        {
            var evidenceIdentity = Path.GetRelativePath(repositoryRoot, result.EvidencePath)
                .Replace('\\', '/');
            await Console.Error.WriteLineAsync(
                $"Test flight recorder preserved: {evidenceIdentity}").ConfigureAwait(false);
            return result.ExitCode;
        }

        try
        {
            ValidateTrx(trxPath, $"{relativeProject}:{trxName}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            await FlightRecorderExecution.PreservePostExitFailureAsync(
                result,
                "trx_validation_failed",
                exception.Message).ConfigureAwait(false);
            var evidenceIdentity = Path.GetRelativePath(repositoryRoot, result.EvidencePath)
                .Replace('\\', '/');
            await Console.Error.WriteLineAsync(
                $"Test flight recorder preserved: {evidenceIdentity}").ConfigureAwait(false);
            return 1;
        }

        await FlightRecorderExecution.DiscardAsync(result).ConfigureAwait(false);
        return 0;
    }

    private static (string ResultsDirectory, string TrxName, string TrxPath) ResolveTrxOutput(
        string repositoryRoot,
        string? requestedResultsDirectory,
        string? requestedTrxName,
        string relativeProject)
    {
        var resultsDirectory = string.IsNullOrWhiteSpace(requestedResultsDirectory)
            ? Path.Combine(repositoryRoot, "artifacts", "test-results")
            : Path.GetFullPath(requestedResultsDirectory, repositoryRoot);
        var trxName = string.IsNullOrWhiteSpace(requestedTrxName)
            ? $"{Path.GetFileNameWithoutExtension(relativeProject)}.trx"
            : ValidateTrxName(requestedTrxName);
        return (resultsDirectory, trxName, Path.Combine(resultsDirectory, trxName));
    }

    private static void ClearStaleTrx(
        (string ResultsDirectory, string TrxName, string TrxPath) trxOutput)
    {
        Directory.CreateDirectory(trxOutput.ResultsDirectory);
        File.Delete(trxOutput.TrxPath);
    }

    private static async Task<int> BuildProjectAsync(
        string projectPath,
        string configuration,
        bool noRestore,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");
        if (noRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }

        return await RunBuildProcessAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> RunBuildProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan? cleanupTimeout = null)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (
                (exception is InvalidOperationException or System.ComponentModel.Win32Exception) &&
                process.HasExited)
            {
                // The build exited between the liveness check and the kill request.
            }

            await process.WaitForExitAsync()
                .WaitAsync(cleanupTimeout ?? TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            throw;
        }

        return process.ExitCode;
    }

    private static ProcessStartInfo CreateVstestStartInfo(
        string projectPath,
        CommandOptions options,
        string? resultsDirectory,
        string trxName)
    {
        var startInfo = CreateDotnetStartInfo();
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(options.Configuration);
        if (options.NoRestore)
        {
            startInfo.ArgumentList.Add("--no-restore");
        }
        startInfo.ArgumentList.Add("--no-build");

        var filter = options.Filter;
        if (string.IsNullOrWhiteSpace(filter) && options.Classes.Length > 0)
        {
            filter = string.Join(
                '|',
                options.Classes
                    .Order(StringComparer.Ordinal)
                    .Distinct(StringComparer.Ordinal)
                    .Select(className => $"FullyQualifiedName~{className}"));
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add(filter);
        }
        if (resultsDirectory is not null)
        {
            startInfo.ArgumentList.Add("--logger");
            startInfo.ArgumentList.Add($"trx;LogFileName={trxName}");
            startInfo.ArgumentList.Add("--results-directory");
            startInfo.ArgumentList.Add(resultsDirectory);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateInProcessXunitStartInfo(
        string projectPath,
        string targetFramework,
        CommandOptions options,
        string? trxPath)
    {
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            throw new InvalidOperationException(
                "The xUnit in-process runner requires --class locators instead of a VSTest filter.");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException("The test project directory is unavailable.");
        var assemblyName = Path.GetFileNameWithoutExtension(projectPath);
        var assemblyPath = Path.Combine(
            projectDirectory,
            "bin",
            options.Configuration,
            targetFramework,
            $"{assemblyName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException("The xUnit in-process test assembly is missing.", assemblyPath);
        }

        var startInfo = CreateDotnetStartInfo();
        startInfo.ArgumentList.Add(assemblyPath);
        startInfo.ArgumentList.Add("-noLogo");
        startInfo.ArgumentList.Add("-noColor");
        startInfo.ArgumentList.Add("-noAutoReporters");
        startInfo.ArgumentList.Add("-reporter");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-parallel");
        startInfo.ArgumentList.Add("none");
        foreach (var className in options.Classes.Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add("-class");
            startInfo.ArgumentList.Add(className);
        }
        if (trxPath is not null)
        {
            startInfo.ArgumentList.Add("-trx");
            startInfo.ArgumentList.Add(trxPath);
        }

        return startInfo;
    }

    private static ProcessStartInfo CreateDotnetStartInfo()
    {
        return new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static string NormalizeProject(string repositoryRoot, string project)
    {
        var fullPath = Path.GetFullPath(project, repositoryRoot);
        return Path.GetRelativePath(repositoryRoot, fullPath).Replace('\\', '/');
    }

    internal static TestProjectDefinition[] DiscoverProjects(string repositoryRoot)
    {
        var testsDirectory = Path.Combine(repositoryRoot, "tests");
        if (!Directory.Exists(testsDirectory))
        {
            throw new DirectoryNotFoundException($"The repository test directory is missing: {testsDirectory}");
        }

        var policyPath = Path.Combine(repositoryRoot, "docs", "testing", "test-runner-policy.json");
        if (!File.Exists(policyPath))
        {
            throw new FileNotFoundException("The test runner policy is missing.", policyPath);
        }

        var policy = JsonSerializer.Deserialize<TestRunnerPolicyDocument>(
            File.ReadAllText(policyPath),
            PolicyJsonOptions) ?? throw new InvalidDataException("The test runner policy is empty.");
        if (policy.SchemaVersion != 1 || policy.Projects is null)
        {
            throw new InvalidDataException("The test runner policy schema is unsupported.");
        }

        var policies = new Dictionary<string, TestRunnerPolicyProject>(StringComparer.Ordinal);
        foreach (var entry in policy.Projects)
        {
            var project = NormalizeProject(repositoryRoot, entry.Project);
            if (!policies.TryAdd(project, entry))
            {
                throw new InvalidDataException($"The test runner policy contains duplicate project '{project}'.");
            }
        }

        var projects = Directory.EnumerateFiles(
                testsDirectory,
                "*.Tests.csproj",
                SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var project = NormalizeProject(repositoryRoot, path);
                var platforms = ReadDeclaredPlatforms(path, project);
                policies.TryGetValue(project, out var runnerPolicy);
                if (runnerPolicy is not null &&
                    !string.Equals(runnerPolicy.Runner, "xunit-in-process", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Test project '{project}' has unsupported runner '{runnerPolicy.Runner}'.");
                }
                if (runnerPolicy is not null && string.IsNullOrWhiteSpace(runnerPolicy.TargetFramework))
                {
                    throw new InvalidDataException(
                        $"Test project '{project}' requires a target framework for xunit-in-process.");
                }

                policies.Remove(project);
                return new TestProjectDefinition(
                    project,
                    platforms,
                    runnerPolicy?.TargetFramework);
            })
            .ToArray();

        if (policies.Count > 0)
        {
            throw new InvalidDataException(
                $"The test runner policy references unknown project(s): {string.Join(", ", policies.Keys.Order(StringComparer.Ordinal))}");
        }

        return projects;
    }

    private static string[] ReadDeclaredPlatforms(string projectPath, string projectIdentity)
    {
        var document = XDocument.Load(projectPath);
        var declarations = document
            .Descendants()
            .Where(element => string.Equals(
                element.Name.LocalName,
                "DownKyiTestPlatforms",
                StringComparison.Ordinal))
            .ToArray();
        if (declarations.Length != 1)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' must declare exactly one DownKyiTestPlatforms value.");
        }

        var declaration = declarations[0];
        if (declaration.AncestorsAndSelf().Any(element =>
                element.Attributes().Any(attribute => string.Equals(
                    attribute.Name.LocalName,
                    "Condition",
                    StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' must declare DownKyiTestPlatforms unconditionally.");
        }

        var platforms = declaration.Value.Split(';')
            .Select(value => value.Trim())
            .ToArray();
        if (platforms.Length == 0 ||
            platforms.Any(string.IsNullOrWhiteSpace) ||
            platforms.Distinct(StringComparer.Ordinal).Count() != platforms.Length)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' has invalid DownKyiTestPlatforms metadata.");
        }

        var unknownPlatforms = platforms
            .Where(platform => platform is not ("Windows" or "Linux" or "macOS"))
            .ToArray();
        if (unknownPlatforms.Length > 0)
        {
            throw new InvalidDataException(
                $"Test project '{projectIdentity}' declares unknown platform(s): {string.Join(", ", unknownPlatforms)}.");
        }

        return platforms;
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Windows";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "Linux";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "macOS";
        }

        throw new PlatformNotSupportedException("The current operating system has no allowlisted test platform.");
    }

    private static string ValidateTrxName(string trxName)
    {
        if (Path.IsPathRooted(trxName) ||
            !string.Equals(Path.GetFileName(trxName), trxName, StringComparison.Ordinal) ||
            trxName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The TRX name must be a file name without directory components.", nameof(trxName));
        }

        return trxName;
    }

    private static void ValidateTrx(string trxPath, string trxIdentity)
    {
        var file = new FileInfo(trxPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidDataException($"The requested TRX is missing or empty: {trxIdentity}");
        }

        var document = XDocument.Load(trxPath);
        if (!string.Equals(document.Root?.Name.LocalName, "TestRun", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The requested TRX has an unexpected root element: {trxIdentity}");
        }

        var counters = document
            .Descendants()
            .FirstOrDefault(element => string.Equals(
                element.Name.LocalName,
                "Counters",
                StringComparison.Ordinal));
        if (counters is null ||
            !int.TryParse(counters.Attribute("executed")?.Value, out var executed) ||
            !int.TryParse(counters.Attribute("failed")?.Value, out var failed) ||
            executed < 1 ||
            failed != 0)
        {
            throw new InvalidDataException(
                $"The requested TRX does not prove a non-empty passing test run: {trxIdentity}");
        }
    }
}

internal sealed record CommandOptions(
    string RepositoryRoot,
    string? Project,
    string Configuration,
    bool NoRestore,
    bool NoBuild,
    string? ResultsDirectory,
    string? TrxName,
    string[] Classes,
    string? Filter,
    int TimeoutSeconds,
    string? EvidenceDirectory)
{
    public static CommandOptions Parse(string[] args)
    {
        var repositoryRoot = Directory.GetCurrentDirectory();
        string? project = null;
        var configuration = "Release";
        var noRestore = false;
        var noBuild = false;
        string? resultsDirectory = null;
        string? trxName = null;
        var classes = new List<string>();
        string? filter = null;
        var timeoutSeconds = 300;
        string? evidenceDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository-root":
                    repositoryRoot = ReadValue(args, ref index);
                    break;
                case "--project":
                    project = ReadValue(args, ref index);
                    break;
                case "--configuration":
                    configuration = ReadValue(args, ref index);
                    break;
                case "--no-restore":
                    noRestore = true;
                    break;
                case "--no-build":
                    noBuild = true;
                    break;
                case "--results-directory":
                    resultsDirectory = ReadValue(args, ref index);
                    break;
                case "--trx-name":
                    trxName = ReadValue(args, ref index);
                    break;
                case "--class":
                    classes.Add(ReadValue(args, ref index));
                    break;
                case "--filter":
                    filter = ReadValue(args, ref index);
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = int.Parse(
                        ReadValue(args, ref index),
                        System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--evidence-directory":
                    evidenceDirectory = ReadValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}", nameof(args));
            }
        }

        if (configuration is not ("Debug" or "Release"))
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Configuration must be Debug or Release.");
        }
        if (timeoutSeconds is < 1 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Timeout must be between 1 and 3600 seconds.");
        }

        return new CommandOptions(
            repositoryRoot,
            project,
            configuration,
            noRestore,
            noBuild,
            resultsDirectory,
            trxName,
            classes.ToArray(),
            filter,
            timeoutSeconds,
            evidenceDirectory);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException("A command option is missing its value.", nameof(args));
        }

        return args[index];
    }
}
