using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacBundleLayoutTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void NonCodePublishFilesMoveToResourcesWithoutBreakingDotNetHost()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-layout-{Guid.NewGuid():N}");
        var publishDirectory = Path.Combine(fixtureRoot, "publish");
        var legacyApp = Path.Combine(fixtureRoot, "Legacy.app");
        var correctedApp = Path.Combine(fixtureRoot, "Corrected.app");

        Directory.CreateDirectory(fixtureRoot);

        try
        {
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException(
                    $"Unsupported macOS test architecture: {RuntimeInformation.ProcessArchitecture}")
            };

            var probeProject = Path.Combine(
                RepositoryRoot,
                "script",
                "macos",
                "fixtures",
                "BundleProbe",
                "BundleProbe.csproj");
            var compilerServerLog = Path.Combine(fixtureRoot, "roslyn-compiler-server.log");
            var publish = Run(
                "dotnet",
                fixtureRoot,
                new Dictionary<string, string?>
                {
                    ["RoslynCommandLineLogFile"] = compilerServerLog
                },
                "publish",
                probeProject,
                "-c",
                "Release",
                "-r",
                $"osx-{architecture}",
                "--self-contained",
                "-p:DebugType=None",
                "-p:DebugSymbols=false",
                "-o",
                publishDirectory);
            AssertSuccess(publish);
            RecordCompilerServerEvidence(publish.ProcessId, compilerServerLog);

            CreateAppBundle(legacyApp, publishDirectory);
            var legacyRuntimeConfig = Path.Combine(
                legacyApp,
                "Contents",
                "MacOS",
                "BundleProbe.runtimeconfig.json");
            var legacyDeps = Path.Combine(
                legacyApp,
                "Contents",
                "MacOS",
                "BundleProbe.deps.json");
            Assert.True(File.Exists(legacyRuntimeConfig));
            Assert.Null(new FileInfo(legacyRuntimeConfig).LinkTarget);
            Assert.True(File.Exists(legacyDeps));
            Assert.Null(new FileInfo(legacyDeps).LinkTarget);

            var legacySigning = RunSigningScript(legacyApp);
            Assert.NotEqual(0, legacySigning.ExitCode);
            var legacyOutput = legacySigning.StandardOutput + legacySigning.StandardError;
            Assert.Contains("code object is not signed at all", legacyOutput, StringComparison.Ordinal);

            CreateAppBundle(correctedApp, publishDirectory);
            AssertSuccess(Run(
                "/bin/bash",
                RepositoryRoot,
                Path.Combine(RepositoryRoot, "script", "macos", "prepare-app-layout.sh"),
                correctedApp));

            var runtimeConfigLink = Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "BundleProbe.runtimeconfig.json");
            var depsLink = Path.Combine(
                correctedApp,
                "Contents",
                "MacOS",
                "BundleProbe.deps.json");
            Assert.NotNull(new FileInfo(runtimeConfigLink).LinkTarget);
            Assert.NotNull(new FileInfo(depsLink).LinkTarget);
            Assert.True(File.Exists(Path.Combine(
                correctedApp,
                "Contents",
                "Resources",
                "dotnet",
                "BundleProbe.runtimeconfig.json")));

            AssertSuccess(RunSigningScript(correctedApp));
            AssertSuccess(Run(
                "/bin/bash",
                RepositoryRoot,
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app.sh"),
                correctedApp));

            var launch = Run(
                Path.Combine(correctedApp, "Contents", "MacOS", "BundleProbe"),
                fixtureRoot);
            AssertSuccess(launch);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void LaunchVerificationBoundsCleanupForTermResistantApp()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-launch-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "Test.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "TestApp");
        Directory.CreateDirectory(executableDirectory);

        try
        {
            File.WriteAllText(
                executablePath,
                "#!/bin/bash\ntrap '' TERM\nwhile true; do :; done\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertSuccess(Run("/bin/chmod", fixtureRoot, "+x", executablePath));

            var stopwatch = Stopwatch.StartNew();
            var result = Run(
                "/bin/bash",
                RepositoryRoot,
                new Dictionary<string, string?>
                {
                    ["MACOS_EXECUTABLE_NAME"] = "TestApp",
                    ["MACOS_LAUNCH_SECONDS"] = "1"
                },
                Path.Combine(RepositoryRoot, "script", "macos", "verify-app-launch.sh"),
                appPath);
            stopwatch.Stop();

            AssertSuccess(result);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                $"Launch cleanup exceeded its bound: {stopwatch.Elapsed}.");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static ProcessResult RunSigningScript(string appPath)
    {
        return Run(
            "/bin/bash",
            RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["MACOS_ADHOC_SIGNING"] = "true"
            },
            Path.Combine(RepositoryRoot, "script", "macos", "sign.sh"),
            appPath);
    }

    private static void CreateAppBundle(string appPath, string publishDirectory)
    {
        var contentsDirectory = Path.Combine(appPath, "Contents");
        var macOsDirectory = Path.Combine(contentsDirectory, "MacOS");
        Directory.CreateDirectory(macOsDirectory);
        Directory.CreateDirectory(Path.Combine(contentsDirectory, "Resources"));

        AssertSuccess(Run("/bin/cp", RepositoryRoot, "-a", $"{publishDirectory}/.", macOsDirectory));
        File.WriteAllText(
            Path.Combine(contentsDirectory, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key>
              <string>BundleProbe</string>
              <key>CFBundleIdentifier</key>
              <string>cn.bzdrs.downkyi.bundle-probe</string>
              <key>CFBundlePackageType</key>
              <string>APPL</string>
            </dict>
            </plist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static ProcessResult Run(
        string fileName,
        string workingDirectory,
        params string[] arguments)
    {
        return Run(fileName, workingDirectory, environment: null, arguments);
    }

    private static ProcessResult Run(
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment != null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process timed out: {fileName}");
        }

        return new ProcessResult(
            process.Id,
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }

    private static void AssertSuccess(ProcessResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Process failed with exit code {result.ExitCode}. stdout={result.StandardOutput} stderr={result.StandardError}");
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Unavailable compiler-server diagnostics cannot replace the publish or outer lease verdict.")]
    private static void RecordCompilerServerEvidence(int invocationProcessId, string logPath)
    {
        try
        {
            var lines = File.Exists(logPath)
                ? File.ReadAllLines(logPath)
                : Array.Empty<string>();
            var clientProcessId = FindLoggedProcessId(lines, "Attempt to open named pipe");
            var serverProcessId = FindLoggedProcessId(lines, "Keep alive timeout is:");
            var keepAliveMilliseconds = FindKeepAliveMilliseconds(lines);
            var serverProcessName = serverProcessId.HasValue
                ? TryGetProcessName(serverProcessId.Value)
                : null;
            var diagnosticMarkers = FindDiagnosticMarkers(lines);

            MacProcessGroupDiagnosticsFixture.RecordCompilerServerEvidence(
                invocationProcessId,
                clientProcessId,
                serverProcessId,
                serverProcessName,
                serverProcessName != null,
                keepAliveMilliseconds,
                diagnosticMarkers,
                diagnosticFailure: null);
        }
        catch (Exception failure)
        {
            MacProcessGroupDiagnosticsFixture.RecordCompilerServerEvidence(
                invocationProcessId,
                clientProcessId: null,
                serverProcessId: null,
                serverProcessName: null,
                serverAliveAfterInvocation: false,
                keepAliveMilliseconds: null,
                diagnosticMarkers: [],
                diagnosticFailure: failure.GetType().Name);
        }
    }

    private static int? FindLoggedProcessId(IEnumerable<string> lines, string marker)
    {
        foreach (var line in lines)
        {
            if (!line.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            const string prefix = "PID";
            var start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += prefix.Length;
            while (start < line.Length && !char.IsAsciiDigit(line[start]))
            {
                start++;
            }
            var end = start;
            while (end < line.Length && char.IsAsciiDigit(line[end]))
            {
                end++;
            }
            if (end > start && int.TryParse(line.AsSpan(start, end - start), out var processId))
            {
                return processId;
            }
        }

        return null;
    }

    private static string[] FindDiagnosticMarkers(IEnumerable<string> lines)
    {
        var markers = new[]
        {
            "Attempt to open named pipe",
            "Keep alive timeout is:"
        };
        var results = new List<string>();
        foreach (var marker in markers)
        {
            var line = lines.FirstOrDefault(candidate => candidate.Contains(marker, StringComparison.Ordinal));
            if (line == null)
            {
                continue;
            }

            var markerEnd = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            results.Add(line[..markerEnd]);
        }

        return results.ToArray();
    }

    private static int? FindKeepAliveMilliseconds(IEnumerable<string> lines)
    {
        const string prefix = "Keep alive timeout is:";
        const string suffix = "milliseconds";
        foreach (var line in lines)
        {
            var start = line.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += prefix.Length;
            var end = line.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end > start && int.TryParse(line.AsSpan(start, end - start).Trim(), out var milliseconds))
            {
                return milliseconds;
            }
        }

        return null;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A disappearing diagnostic process is reported as unavailable, not a correctness result.")]
    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
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

    private sealed record ProcessResult(
        int ProcessId,
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
