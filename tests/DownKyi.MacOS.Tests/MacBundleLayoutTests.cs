using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DownKyi.TestInfrastructure;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacBundleLayoutTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly TimeSpan ProcessExecutionTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NonCodePublishFilesMoveToResourcesWithoutBreakingDotNetHost()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-layout-{Guid.NewGuid():N}");
        var publishDirectory = Path.Combine(fixtureRoot, "publish");
        var legacyApp = Path.Combine(fixtureRoot, "Legacy.app");
        var correctedApp = Path.Combine(fixtureRoot, "Corrected.app");
        var cancellationToken = TestContext.Current.CancellationToken;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                Directory.CreateDirectory(fixtureRoot);

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
                AssertSuccess(await RunAsync(
                    "dotnet",
                    fixtureRoot,
                    cancellationToken,
                    "publish",
                    probeProject,
                    "-c",
                    "Release",
                    "-r",
                    $"osx-{architecture}",
                    "--self-contained",
                    "-nodeReuse:false",
                    "-p:UseSharedCompilation=false",
                    "-p:DebugType=None",
                    "-p:DebugSymbols=false",
                    "-o",
                    publishDirectory).ConfigureAwait(false));

                await CreateAppBundleAsync(
                    legacyApp,
                    publishDirectory,
                    cancellationToken).ConfigureAwait(false);
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

                var legacySigning = await RunSigningScriptAsync(
                    legacyApp,
                    cancellationToken).ConfigureAwait(false);
                Assert.NotEqual(0, legacySigning.ExitCode);
                var legacyOutput = legacySigning.StandardOutput + legacySigning.StandardError;
                Assert.Contains("code object is not signed at all", legacyOutput, StringComparison.Ordinal);

                await CreateAppBundleAsync(
                    correctedApp,
                    publishDirectory,
                    cancellationToken).ConfigureAwait(false);
                AssertSuccess(await RunAsync(
                    "/bin/bash",
                    RepositoryRoot,
                    cancellationToken,
                    Path.Combine(RepositoryRoot, "script", "macos", "prepare-app-layout.sh"),
                    correctedApp).ConfigureAwait(false));

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

                AssertSuccess(await RunSigningScriptAsync(
                    correctedApp,
                    cancellationToken).ConfigureAwait(false));
                AssertSuccess(await RunAsync(
                    "/bin/bash",
                    RepositoryRoot,
                    cancellationToken,
                    Path.Combine(RepositoryRoot, "script", "macos", "verify-app.sh"),
                    correctedApp).ConfigureAwait(false));

                var launch = await RunAsync(
                    Path.Combine(correctedApp, "Contents", "MacOS", "BundleProbe"),
                    fixtureRoot,
                    cancellationToken).ConfigureAwait(false);
                AssertSuccess(launch);
            },
            () => DeleteDirectoryAsync(fixtureRoot)).ConfigureAwait(true);
    }

    [Fact]
    public async Task LaunchVerificationBoundsCleanupForTermResistantApp()
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-launch-{Guid.NewGuid():N}");
        var appPath = Path.Combine(fixtureRoot, "Test.app");
        var executableDirectory = Path.Combine(appPath, "Contents", "MacOS");
        var executablePath = Path.Combine(executableDirectory, "TestApp");
        var cancellationToken = TestContext.Current.CancellationToken;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                Directory.CreateDirectory(executableDirectory);

                await File.WriteAllTextAsync(
                    executablePath,
                    "#!/bin/bash\ntrap '' TERM\nwhile true; do sleep 1; done\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);
                AssertSuccess(await RunAsync(
                    "/bin/chmod",
                    fixtureRoot,
                    cancellationToken,
                    "+x",
                    executablePath).ConfigureAwait(false));

                var stopwatch = Stopwatch.StartNew();
                var result = await RunAsync(
                    "/bin/bash",
                    RepositoryRoot,
                    new Dictionary<string, string?>
                    {
                        ["MACOS_EXECUTABLE_NAME"] = "TestApp",
                        ["MACOS_LAUNCH_SECONDS"] = "1"
                    },
                    cancellationToken,
                    Path.Combine(RepositoryRoot, "script", "macos", "verify-app-launch.sh"),
                    appPath).ConfigureAwait(false);
                stopwatch.Stop();

                AssertSuccess(result);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(15),
                    $"Launch cleanup exceeded its bound: {stopwatch.Elapsed}.");
            },
            () => DeleteDirectoryAsync(fixtureRoot)).ConfigureAwait(true);
    }

    private static Task<ExternalProcessResult> RunSigningScriptAsync(
        string appPath,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            "/bin/bash",
            RepositoryRoot,
            new Dictionary<string, string?>
            {
                ["MACOS_ADHOC_SIGNING"] = "true"
            },
            cancellationToken,
            Path.Combine(RepositoryRoot, "script", "macos", "sign.sh"),
            appPath);
    }

    private static async Task CreateAppBundleAsync(
        string appPath,
        string publishDirectory,
        CancellationToken cancellationToken)
    {
        var contentsDirectory = Path.Combine(appPath, "Contents");
        var macOsDirectory = Path.Combine(contentsDirectory, "MacOS");
        Directory.CreateDirectory(macOsDirectory);
        Directory.CreateDirectory(Path.Combine(contentsDirectory, "Resources"));

        AssertSuccess(await RunAsync(
            "/bin/cp",
            RepositoryRoot,
            cancellationToken,
            "-a",
            $"{publishDirectory}/.",
            macOsDirectory).ConfigureAwait(false));
        await File.WriteAllTextAsync(
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
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<ExternalProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        return RunAsync(fileName, workingDirectory, null, cancellationToken, arguments);
    }

    private static Task<ExternalProcessResult> RunAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken,
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

        return ExternalProcessTestHarness.RunAsync(
            startInfo,
            ProcessExecutionTimeout,
            ProcessCleanupTimeout,
            cancellationToken);
    }

    private static void AssertSuccess(ExternalProcessResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Process failed with exit code {result.ExitCode}. stdout={result.StandardOutput} stderr={result.StandardError}");
    }

    private static Task DeleteDirectoryAsync(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
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
}
