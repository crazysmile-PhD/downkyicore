using System.Diagnostics;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CentralTestRunnerCommandTests
{
    [Fact]
    public async Task BuildCancellationReturns130AfterStoppingOwnedBuildProcess()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        var markerPath = Path.Combine(repositoryRoot, "build-process.pid");
        int? processId = null;
        await FailurePreservingTestCleanup.RunAsync(
            async () =>
            {
                var projectDirectory = Path.Combine(repositoryRoot, "tests", "Fixture.Tests");
                var projectPath = Path.Combine(projectDirectory, "Fixture.Tests.csproj");
                Directory.CreateDirectory(projectDirectory);
                var runnerAssembly = typeof(FlightRecorderExecution).Assembly.Location;
                var runtimeConfig = Path.Combine(
                    AppContext.BaseDirectory,
                    "DownKyi.Architecture.Tests.runtimeconfig.json");
                var fixtureWorkingDirectory = AppContext.BaseDirectory;
                var command =
                    $"dotnet exec --runtimeconfig &quot;{EscapeXml(runtimeConfig)}&quot; " +
                    $"&quot;{EscapeXml(runnerAssembly)}&quot; fixture-hold-marker &quot;{EscapeXml(markerPath)}&quot;";
                await File.WriteAllTextAsync(
                    projectPath,
                    $$"""
                    <Project DefaultTargets="Build">
                      <PropertyGroup>
                        <DownKyiTestPlatforms>Windows;Linux;macOS</DownKyiTestPlatforms>
                      </PropertyGroup>
                      <Target Name="Build">
                        <Exec Command="{{command}}" WorkingDirectory="{{EscapeXml(fixtureWorkingDirectory)}}" />
                      </Target>
                    </Project>
                    """,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                using var cancellation = new CancellationTokenSource();
                var run = Program.RunCommandAsync(
                    [
                        "run-project",
                        "--repository-root", repositoryRoot,
                        "--project", "tests/Fixture.Tests/Fixture.Tests.csproj",
                        "--configuration", "Release",
                        "--no-restore"
                ],
                    cancellation.Token);
                processId = await WaitForProcessMarkerAsync(markerPath).ConfigureAwait(true);
                File.Delete(projectPath);
                Assert.False(File.Exists(projectPath));

                var cancellationRequestedUtc = DateTimeOffset.UtcNow;
                await cancellation.CancelAsync().ConfigureAwait(true);
                var exitCode = await run.ConfigureAwait(true);

                Assert.Equal(130, exitCode);
                Assert.False(IsProcessAlive(processId.Value));
                try
                {
                    Directory.Delete(projectDirectory);
                }
                catch (IOException exception) when (
                    OperatingSystem.IsWindows() && IsSharingViolation(exception))
                {
                    var evidence = WindowsDirectoryHandleForensics.Capture(
                        projectDirectory,
                        processId.Value,
                        Environment.ProcessId,
                        cancellationRequestedUtc);
                    throw new IOException(
                        $"{exception.Message}{Environment.NewLine}" +
                        $"Failure-only handle forensics:{Environment.NewLine}{evidence}",
                        exception);
                }
                Assert.False(Directory.Exists(projectDirectory));
            },
            () =>
            {
                if (processId is not null)
                {
                    StopProcessIfAlive(processId.Value);
                }

                Directory.Delete(repositoryRoot, recursive: true);
                return Task.CompletedTask;
            }).ConfigureAwait(true);
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xffff) is 32 or 33;

    [Fact]
    public async Task RunSolutionRejectsEmptyProjectDiscovery()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No runnable test projects", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionRejectsEmptyCurrentPlatformSelection()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            var unsupportedPlatform = OperatingSystem.IsWindows() ? "Linux" : "Windows";
            await WriteProjectAsync(repositoryRoot, "Fixture.Tests", unsupportedPlatform, failBuild: false);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => RunSolutionAsync(repositoryRoot));

            Assert.Contains("No test projects support", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunSolutionClearsEverySelectedTrxBeforeFirstBuildFailure()
    {
        var repositoryRoot = await CreateRepositoryAsync();
        try
        {
            await WriteProjectAsync(
                repositoryRoot,
                "A.Tests",
                "Windows;Linux;macOS",
                failBuild: true);
            await WriteProjectAsync(
                repositoryRoot,
                "Z.Tests",
                "Windows;Linux;macOS",
                failBuild: false);
            var resultsDirectory = Path.Combine(repositoryRoot, "results");
            Directory.CreateDirectory(resultsDirectory);
            var firstTrx = Path.Combine(resultsDirectory, "A.Tests.trx");
            var laterTrx = Path.Combine(resultsDirectory, "Z.Tests.trx");
            await File.WriteAllTextAsync(firstTrx, "stale-pass", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(laterTrx, "stale-pass", TestContext.Current.CancellationToken);

            var exitCode = await RunSolutionAsync(repositoryRoot, resultsDirectory);

            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(firstTrx));
            Assert.False(File.Exists(laterTrx));
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    private static Task<int> RunSolutionAsync(string repositoryRoot, string? resultsDirectory = null)
    {
        var arguments = new List<string>
        {
            "run-solution",
            "--repository-root", repositoryRoot,
            "--configuration", "Release",
            "--no-restore"
        };
        if (resultsDirectory is not null)
        {
            arguments.Add("--results-directory");
            arguments.Add(resultsDirectory);
        }

        return CentralTestCommand.RunAsync(arguments.ToArray(), TestContext.Current.CancellationToken);
    }

    private static async Task<string> CreateRepositoryAsync()
    {
        var repositoryRoot = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-central-runner-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests"));
        var policyDirectory = Path.Combine(repositoryRoot, "docs", "testing");
        Directory.CreateDirectory(policyDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(policyDirectory, "test-runner-policy.json"),
            """{"schemaVersion":1,"projects":[]}""",
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        return repositoryRoot;
    }

    private static Task WriteProjectAsync(
        string repositoryRoot,
        string projectName,
        string platforms,
        bool failBuild)
    {
        var projectDirectory = Path.Combine(repositoryRoot, "tests", projectName);
        Directory.CreateDirectory(projectDirectory);
        var buildTarget = failBuild
            ? "<Error Text=\"intentional first project failure\" />"
            : string.Empty;
        return File.WriteAllTextAsync(
            Path.Combine(projectDirectory, $"{projectName}.csproj"),
            $$"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <DownKyiTestPlatforms>{{platforms}}</DownKyiTestPlatforms>
              </PropertyGroup>
              <Target Name="Build">
                {{buildTarget}}
              </Target>
            </Project>
            """,
            TestContext.Current.CancellationToken);
    }

    private static async Task<int> WaitForProcessMarkerAsync(string markerPath)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(markerPath) &&
                    int.TryParse(
                        await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken)
                            .ConfigureAwait(false),
                        out var processId))
                {
                    return processId;
                }
            }
            catch (IOException)
            {
                // The fixture created the marker but has not closed its write handle yet.
            }

            await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The build fixture did not publish its process identity.");
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void StopProcessIfAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (ArgumentException)
        {
            // The cancellation path already stopped the build fixture.
        }
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }
}
