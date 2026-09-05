using System.Diagnostics;
using System.Runtime.Versioning;
using DownKyi.TestInfrastructure;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacReleasePackageValidationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] RuntimeRelativePaths =
        ["DownKyi", "aria2/aria2c", "ffmpeg/ffmpeg", "ffmpeg/ffprobe"];
    private static readonly TimeSpan ProcessExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BundleMetadataUsesReleaseVersion()
    {
        var root = CreateTemporaryDirectory();
        var plist = Path.Combine(root, "Info.plist");
        var cancellationToken = TestContext.Current.CancellationToken;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                File.Copy(Path.Combine(RepositoryRoot, "script", "macos", "Info.plist"), plist);
                var result = await RunAsync(
                    "/bin/bash",
                    [Path.Combine(RepositoryRoot, "script", "macos", "set-bundle-version.sh"), plist, "1.1.3"],
                    root,
                    cancellationToken).ConfigureAwait(false);
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(
                    "1.1.3",
                    await ReadPlistValueAsync(
                        plist,
                        "CFBundleShortVersionString",
                        cancellationToken).ConfigureAwait(false));
                Assert.Equal(
                    "1.1.3",
                    await ReadPlistValueAsync(
                        plist,
                        "CFBundleVersion",
                        cancellationToken).ConfigureAwait(false));
            },
            () => DeleteDirectoryAsync(root)).ConfigureAwait(true);
    }

    [Fact]
    public async Task RuntimeArchitectureValidatorRejectsOppositeMachO()
    {
        var root = CreateTemporaryDirectory();
        var app = Path.Combine(root, "Fixture.app");
        var runtime = Path.Combine(app, "Contents", "MacOS");
        var cancellationToken = TestContext.Current.CancellationToken;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                const string fixtureArchitecture = "x86_64";
                const string actualRid = "osx-x64";
                const string oppositeRid = "osx-arm64";
                var sourceBinary = "/usr/bin/true";
                var sourceArchitectures = (await RunAsync(
                        "/usr/bin/lipo",
                    ["-archs", sourceBinary],
                    root,
                    cancellationToken).ConfigureAwait(false))
                    .StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Assert.Contains(fixtureArchitecture, sourceArchitectures);
                foreach (var relativePath in RuntimeRelativePaths)
                {
                    var path = Path.Combine(runtime, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (sourceArchitectures.Length == 1)
                    {
                        File.Copy(sourceBinary, path);
                    }
                    else
                    {
                        Assert.Equal(
                            0,
                            (await RunAsync(
                                "/usr/bin/lipo",
                                [sourceBinary, "-thin", fixtureArchitecture, "-output", path],
                                root,
                                cancellationToken).ConfigureAwait(false)).ExitCode);
                    }
                }

                var validator = Path.Combine(RepositoryRoot, "script", "macos", "verify-runtime-architecture.sh");

                Assert.Equal(
                    0,
                    (await RunAsync(
                        "/bin/bash",
                        [validator, app, actualRid],
                        root,
                        cancellationToken).ConfigureAwait(false)).ExitCode);
                var mismatch = await RunAsync(
                    "/bin/bash",
                    [validator, app, oppositeRid],
                    root,
                    cancellationToken).ConfigureAwait(false);
                Assert.NotEqual(0, mismatch.ExitCode);
                Assert.Contains("expected", mismatch.StandardError, StringComparison.Ordinal);
            },
            () => DeleteDirectoryAsync(root)).ConfigureAwait(true);
    }

    [Fact]
    public void MountedDmgVerificationOwnsVersionAndArchitectureChecks()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "macos",
            "verify-dmg-contents.sh"));

        Assert.Contains("CFBundleShortVersionString", script, StringComparison.Ordinal);
        Assert.Contains("CFBundleVersion", script, StringComparison.Ordinal);
        Assert.Contains("verify-runtime-architecture.sh", script, StringComparison.Ordinal);
    }

    private static async Task<string> ReadPlistValueAsync(
        string plist,
        string key,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "/usr/libexec/PlistBuddy",
            ["-c", $"Print :{key}", plist],
            Path.GetDirectoryName(plist)!,
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    private static Task<ExternalProcessResult> RunAsync(
        string executable,
        string[] arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
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

        return ExternalProcessTestHarness.RunAsync(
            startInfo,
            ProcessExecutionTimeout,
            ProcessCleanupTimeout,
            cancellationToken);
    }

    private static Task DeleteDirectoryAsync(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-mac-release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DownKyi.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
