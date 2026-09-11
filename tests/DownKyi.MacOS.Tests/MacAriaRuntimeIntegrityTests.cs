using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacAriaRuntimeIntegrityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string IntegrityScript = Path.Combine(
        RepositoryRoot,
        "script",
        "macos",
        "aria2-runtime-integrity.sh");

    [Fact]
    public void SigningFinalizesRuntimeChecksumBeforeOuterSealAndSurvivesDmgCopy()
    {
        var fixtureRoot = CreateTemporaryDirectory();
        var appPath = Path.Combine(fixtureRoot, "Fixture.app");
        var runtimeIdentifier = CurrentRuntimeIdentifier();

        try
        {
            CreateAppFixture(fixtureRoot, appPath);
            AssertSuccess(Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier));

            var ariaExecutable = Path.Combine(appPath, "Contents", "MacOS", "aria2", "aria2c");
            var originalHash = HashFile(ariaExecutable);
            AssertSuccess(Run("/usr/bin/codesign", fixtureRoot, "--force", "--sign", "-", ariaExecutable));
            Assert.NotEqual(originalHash, HashFile(ariaExecutable));

            var staleAfterNestedSigning = Run(
                "/bin/bash",
                fixtureRoot,
                IntegrityScript,
                "verify",
                appPath,
                runtimeIdentifier);
            AssertFailureContains(staleAfterNestedSigning, "does not match runtime sidecar");

            AssertSuccess(Run("/bin/bash", fixtureRoot, IntegrityScript, "refresh", appPath));
            AssertSuccess(Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier));

            AssertSuccess(Run(
                "/bin/bash",
                fixtureRoot,
                new Dictionary<string, string?> { ["MACOS_ADHOC_SIGNING"] = "true" },
                Path.Combine(RepositoryRoot, "script", "macos", "sign.sh"),
                appPath));
            AssertSuccess(Run("/usr/bin/codesign", fixtureRoot, "--verify", "--deep", "--strict", appPath));
            AssertSuccess(Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier));
            AssertFailureContains(
                Run("/bin/bash", fixtureRoot, IntegrityScript, "refresh", appPath),
                "refusing to modify the runtime checksum after the outer app signature is sealed");

            var copiedApp = AssertDmgAndInstalledCopyRoundtrip(
                fixtureRoot,
                appPath,
                runtimeIdentifier);

            File.AppendAllText(
                Path.Combine(copiedApp, "Contents", "MacOS", "aria2", "aria2c"),
                "tamper",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertFailureContains(
                Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", copiedApp, runtimeIdentifier),
                "does not match runtime sidecar");

            File.WriteAllText(
                Path.Combine(appPath, "Contents", "Resources", "dotnet", "aria2", "aria2c.sha256"),
                new string('0', 64),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            AssertFailureContains(
                Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier),
                "does not match runtime sidecar");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public void IntegrityBoundaryRejectsMalformedOrReplacedChecksumSymlink()
    {
        var fixtureRoot = CreateTemporaryDirectory();
        var appPath = Path.Combine(fixtureRoot, "Fixture.app");
        var runtimeIdentifier = CurrentRuntimeIdentifier();

        try
        {
            CreateAppFixture(fixtureRoot, appPath);
            var checksumTarget = Path.Combine(
                appPath,
                "Contents",
                "Resources",
                "dotnet",
                "aria2",
                "aria2c.sha256");
            File.WriteAllText(checksumTarget, "not-a-digest");
            AssertFailureContains(
                Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier),
                "exactly one 64-character SHA-256 digest");

            var checksumLink = Path.Combine(appPath, "Contents", "MacOS", "aria2", "aria2c.sha256");
            File.Delete(checksumLink);
            File.WriteAllText(checksumLink, HashFile(Path.Combine(
                appPath,
                "Contents",
                "MacOS",
                "aria2",
                "aria2c")));
            AssertFailureContains(
                Run("/bin/bash", fixtureRoot, IntegrityScript, "verify", appPath, runtimeIdentifier),
                "must remain a symlink");
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static string AssertDmgAndInstalledCopyRoundtrip(
        string fixtureRoot,
        string appPath,
        string runtimeIdentifier)
    {
        var stagingDirectory = Path.Combine(fixtureRoot, "dmg-staging");
        var stagedApp = Path.Combine(stagingDirectory, Path.GetFileName(appPath));
        var dmgPath = Path.Combine(fixtureRoot, "fixture.dmg");
        var mountPoint = Path.Combine(fixtureRoot, "mounted");
        var copiedApp = Path.Combine(fixtureRoot, "installed", Path.GetFileName(appPath));
        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(mountPoint);
        Directory.CreateDirectory(Path.GetDirectoryName(copiedApp)!);

        AssertSuccess(Run("/usr/bin/ditto", fixtureRoot, appPath, stagedApp));
        AssertSuccess(Run(
            "/usr/bin/hdiutil",
            fixtureRoot,
            "create",
            "-quiet",
            "-fs",
            "HFS+",
            "-volname",
            "DownKyiIntegrityFixture",
            "-srcfolder",
            stagingDirectory,
            dmgPath));
        AssertSuccess(Run(
            "/usr/bin/hdiutil",
            fixtureRoot,
            "attach",
            "-readonly",
            "-nobrowse",
            "-mountpoint",
            mountPoint,
            dmgPath));

        try
        {
            var mountedApp = Path.Combine(mountPoint, Path.GetFileName(appPath));
            AssertSuccess(Run(
                "/bin/bash",
                fixtureRoot,
                IntegrityScript,
                "verify",
                mountedApp,
                runtimeIdentifier));
            AssertSuccess(Run("/usr/bin/codesign", fixtureRoot, "--verify", "--deep", "--strict", mountedApp));
            AssertSuccess(Run("/usr/bin/ditto", fixtureRoot, mountedApp, copiedApp));
        }
        finally
        {
            AssertSuccess(Run("/usr/bin/hdiutil", fixtureRoot, "detach", "-quiet", mountPoint));
        }

        AssertSuccess(Run(
            "/bin/bash",
            fixtureRoot,
            IntegrityScript,
            "verify",
            copiedApp,
            runtimeIdentifier));
        AssertSuccess(Run("/usr/bin/codesign", fixtureRoot, "--verify", "--deep", "--strict", copiedApp));
        return copiedApp;
    }

    private static void CreateAppFixture(string fixtureRoot, string appPath)
    {
        var contentsDirectory = Path.Combine(appPath, "Contents");
        var macOsDirectory = Path.Combine(contentsDirectory, "MacOS");
        var ariaDirectory = Path.Combine(macOsDirectory, "aria2");
        var resourceDirectory = Path.Combine(contentsDirectory, "Resources", "dotnet", "aria2");
        Directory.CreateDirectory(ariaDirectory);
        Directory.CreateDirectory(resourceDirectory);

        var sourcePath = Path.Combine(fixtureRoot, "fixture.c");
        var unsignedMachO = Path.Combine(fixtureRoot, "fixture-macho");
        File.WriteAllText(sourcePath, "int main(void) { return 0; }\n");
        AssertSuccess(Run(
            "/usr/bin/clang",
            fixtureRoot,
            "-arch",
            CurrentArchitecture(),
            sourcePath,
            "-o",
            unsignedMachO));
        // Apple Silicon linkers commonly add an ad-hoc signature while Intel
        // linkers may leave the fixture unsigned. Either state is a valid
        // source baseline; the explicit signing step below remains mandatory.
        _ = Run("/usr/bin/codesign", fixtureRoot, "--remove-signature", unsignedMachO);

        var mainExecutable = Path.Combine(macOsDirectory, "DownKyi");
        var ariaExecutable = Path.Combine(ariaDirectory, "aria2c");
        File.Copy(unsignedMachO, mainExecutable);
        File.Copy(unsignedMachO, ariaExecutable);
        AssertSuccess(Run("/bin/chmod", fixtureRoot, "+x", mainExecutable, ariaExecutable));

        var checksumTarget = Path.Combine(resourceDirectory, "aria2c.sha256");
        File.WriteAllText(checksumTarget, HashFile(ariaExecutable));
        File.CreateSymbolicLink(
            Path.Combine(ariaDirectory, "aria2c.sha256"),
            "../../Resources/dotnet/aria2/aria2c.sha256");
        File.WriteAllText(
            Path.Combine(contentsDirectory, "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleExecutable</key>
              <string>DownKyi</string>
              <key>CFBundleIdentifier</key>
              <string>cn.bzdrs.downkyi.integrity-fixture</string>
              <key>CFBundlePackageType</key>
              <string>APPL</string>
            </dict>
            </plist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string CurrentRuntimeIdentifier() => $"osx-{RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Unsupported macOS architecture: {RuntimeInformation.ProcessArchitecture}")
    }}";

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "arm64",
        _ => throw new PlatformNotSupportedException(
            $"Unsupported macOS architecture: {RuntimeInformation.ProcessArchitecture}")
    };

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void AssertFailureContains(ProcessResult result, string expected)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    private static ProcessResult Run(
        string fileName,
        string workingDirectory,
        params string[] arguments) =>
        Run(fileName, workingDirectory, environment: null, arguments);

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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process timed out: {fileName}");
        }

        return new ProcessResult(
            process.ExitCode,
            output.GetAwaiter().GetResult(),
            error.GetAwaiter().GetResult());
    }

    private static void AssertSuccess(ProcessResult result)
    {
        Assert.True(
            result.ExitCode == 0,
            $"Process failed with exit code {result.ExitCode}. stdout={result.StandardOutput} stderr={result.StandardError}");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-aria-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
