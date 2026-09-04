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
        try
        {
            var projectDirectory = Path.Combine(repositoryRoot, "tests", "Fixture.Tests");
            Directory.CreateDirectory(projectDirectory);
            var runnerAssembly = typeof(FlightRecorderExecution).Assembly.Location;
            var runtimeConfig = Path.Combine(
                AppContext.BaseDirectory,
                "DownKyi.Architecture.Tests.runtimeconfig.json");
            var command =
                $"dotnet exec --runtimeconfig &quot;{EscapeXml(runtimeConfig)}&quot; " +
                $"&quot;{EscapeXml(runnerAssembly)}&quot; fixture-hold-marker &quot;{EscapeXml(markerPath)}&quot;";
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "Fixture.Tests.csproj"),
                $$"""
                <Project DefaultTargets="Build">
                  <PropertyGroup>
                    <DownKyiTestPlatforms>Windows;Linux;macOS</DownKyiTestPlatforms>
                  </PropertyGroup>
                  <Target Name="Build">
                    <Exec Command="{{command}}" />
                  </Target>
                </Project>
                """,
                TestContext.Current.CancellationToken);

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
            processId = await WaitForProcessMarkerAsync(markerPath);

            await cancellation.CancelAsync();
            var exitCode = await run.ConfigureAwait(true);

            Assert.Equal(130, exitCode);
            var isProcessAlive = IsProcessAlive(processId.Value);
            Assert.False(
                isProcessAlive,
                isProcessAlive ? DescribeUnixProcessState(processId.Value) : string.Empty);
            try
            {
                Directory.Delete(projectDirectory, recursive: true);
            }
            catch (IOException exception) when (OperatingSystem.IsWindows())
            {
                throw new IOException(
                    $"{exception.Message}{Environment.NewLine}" +
                    $"Fixture-process snapshot after cancellation:{Environment.NewLine}" +
                    CaptureWindowsFixtureProcesses(projectDirectory, markerPath),
                    exception);
            }
            Assert.False(Directory.Exists(projectDirectory));
        }
        finally
        {
            if (processId is not null)
            {
                StopProcessIfAlive(processId.Value);
            }
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

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

    private static string DescribeUnixProcessState(int processId)
    {
        if (OperatingSystem.IsWindows())
        {
            return "The marker process is still alive on Windows.";
        }

        var startInfo = new ProcessStartInfo("/bin/ps")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("pid=,ppid=,stat=,comm=");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var state = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Unix process-state probe did not start.");
        var standardOutput = state.StandardOutput.ReadToEnd();
        var standardError = state.StandardError.ReadToEnd();
        if (!state.WaitForExit(3000))
        {
            throw new TimeoutException("The Unix process-state probe did not exit.");
        }
        if (state.ExitCode != 0)
        {
            throw new InvalidOperationException($"The Unix process-state probe failed: {standardError.Trim()}");
        }

        return string.IsNullOrWhiteSpace(standardOutput) ? "The marker process was not listed." : standardOutput.Trim();
    }

    private static string CaptureWindowsFixtureProcesses(string projectDirectory, string markerPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "not applicable";
        }

        return RunPowerShellProcessQuery(
            """
            param([string] $projectDirectory, [string] $markerPath)
            Get-CimInstance Win32_Process |
              Where-Object {
                $_.CommandLine -like "*$projectDirectory*" -or
                $_.CommandLine -like "*$markerPath*"
              } |
              ForEach-Object {
                '{0}|{1}|{2}|{3}' -f $_.ProcessId,$_.ParentProcessId,$_.Name,$_.CommandLine
              }
            """,
            projectDirectory,
            markerPath);
    }

    private static string RunPowerShellProcessQuery(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var query = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Windows process query did not start.");
        var standardOutput = query.StandardOutput.ReadToEnd();
        var standardError = query.StandardError.ReadToEnd();
        if (!query.WaitForExit(3000))
        {
            throw new TimeoutException("The Windows process query did not exit.");
        }
        if (query.ExitCode != 0)
        {
            throw new InvalidOperationException($"The Windows process query failed: {standardError.Trim()}");
        }

        return string.IsNullOrWhiteSpace(standardOutput) ? "<none>" : standardOutput.Trim();
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
