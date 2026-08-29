using System.Diagnostics;
using System.Runtime.Versioning;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacReleasePackageValidationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BundleMetadataUsesReleaseVersion()
    {
        var root = CreateTemporaryDirectory();
        var plist = Path.Combine(root, "Info.plist");
        try
        {
            File.Copy(Path.Combine(RepositoryRoot, "script", "macos", "Info.plist"), plist);
            var result = Run(
                "/bin/bash",
                [Path.Combine(RepositoryRoot, "script", "macos", "set-bundle-version.sh"), plist, "1.1.3"],
                root);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("1.1.3", ReadPlistValue(plist, "CFBundleShortVersionString"));
            Assert.Equal("1.1.3", ReadPlistValue(plist, "CFBundleVersion"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeArchitectureValidatorRejectsOppositeMachO()
    {
        var root = CreateTemporaryDirectory();
        var app = Path.Combine(root, "Fixture.app");
        var runtime = Path.Combine(app, "Contents", "MacOS");
        try
        {
            var machine = Run("/usr/bin/uname", ["-m"], root).StandardOutput.Trim();
            var actualRid = machine == "arm64" ? "osx-arm64" : "osx-x64";
            var oppositeRid = machine == "arm64" ? "osx-x64" : "osx-arm64";
            var sourceBinary = "/usr/bin/true";
            var sourceArchitectures = Run("/usr/bin/lipo", ["-archs", sourceBinary], root)
                .StandardOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(machine, sourceArchitectures);
            foreach (var relativePath in new[] { "DownKyi", "aria2/aria2c", "ffmpeg/ffmpeg", "ffmpeg/ffprobe" })
            {
                var path = Path.Combine(runtime, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (sourceArchitectures.Length == 1)
                {
                    File.Copy(sourceBinary, path);
                }
                else
                {
                    Assert.Equal(0, Run("/usr/bin/lipo", [sourceBinary, "-thin", machine, "-output", path], root).ExitCode);
                }
            }

            var validator = Path.Combine(RepositoryRoot, "script", "macos", "verify-runtime-architecture.sh");

            Assert.Equal(0, Run("/bin/bash", [validator, app, actualRid], root).ExitCode);
            var mismatch = Run("/bin/bash", [validator, app, oppositeRid], root);
            Assert.NotEqual(0, mismatch.ExitCode);
            Assert.Contains("expected", mismatch.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static string ReadPlistValue(string plist, string key) =>
        Run("/usr/libexec/PlistBuddy", ["-c", $"Print :{key}", plist], Path.GetDirectoryName(plist)!)
            .StandardOutput.Trim();

    private static ProcessResult Run(string executable, string[] arguments, string workingDirectory)
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

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {executable}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output, error);
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
