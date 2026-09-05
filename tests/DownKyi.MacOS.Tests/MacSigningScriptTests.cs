using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using DownKyi.TestInfrastructure;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacSigningScriptTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] SigningFixtureFileNames =
        ["sign.sh", "codesign-common.sh", "DownKyi.entitlements"];
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AdHocSigningExecutesUnderSystemBashNounsetWithoutTimestamp()
    {
        var calls = await RunSigningFixtureAsync(
            adHoc: true,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AssertSigningCoverage(calls);
        Assert.All(calls, arguments =>
        {
            Assert.DoesNotContain("--timestamp", arguments);
            AssertCodesignIdentity(arguments, "-");
        });
    }

    [Fact]
    public async Task DeveloperIdSigningIncludesTimestamp()
    {
        const string identity = "Developer ID Application: DownKyi Test";
        var calls = await RunSigningFixtureAsync(
            adHoc: false,
            TestContext.Current.CancellationToken,
            identity).ConfigureAwait(true);

        AssertSigningCoverage(calls);
        Assert.All(calls, arguments =>
        {
            Assert.Contains("--timestamp", arguments);
            AssertCodesignIdentity(arguments, identity);
        });
    }

    private static async Task<string[][]> RunSigningFixtureAsync(
        bool adHoc,
        CancellationToken cancellationToken,
        string identity = "Developer ID Application: DownKyi Test")
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-signing-{Guid.NewGuid():N}");
        var stubDirectory = Path.Combine(fixtureRoot, "stub-bin");
        var appContentsDirectory = Path.Combine(fixtureRoot, "Test.app", "Contents");
        var appBinaryDirectory = Path.Combine(appContentsDirectory, "MacOS");
        var codesignLog = Path.Combine(fixtureRoot, "codesign.log");
        string[][]? calls = null;

        await ExternalProcessTestHarness.RunWithCleanupAsync(
            async () =>
            {
                Directory.CreateDirectory(stubDirectory);
                Directory.CreateDirectory(appBinaryDirectory);

                foreach (var fileName in SigningFixtureFileNames)
                {
                    var source = await File.ReadAllTextAsync(
                        Path.Combine(RepositoryRoot, "script", "macos", fileName),
                        cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(
                        Path.Combine(fixtureRoot, fileName),
                        source.Replace("\r\n", "\n", StringComparison.Ordinal),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        cancellationToken).ConfigureAwait(false);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(stubDirectory, "file"),
                    """
                #!/bin/bash
                set -eu
                case "$1" in
                  */DownKyi|*.dylib)
                    printf '%s: Mach-O 64-bit executable\n' "$1"
                    ;;
                  *.dll)
                    printf '%s: PE32 executable Mono/.Net assembly\n' "$1"
                    ;;
                  *)
                    printf '%s: ASCII text\n' "$1"
                    ;;
                esac
                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(stubDirectory, "codesign"),
                    """
                #!/bin/bash
                set -eu
                {
                  printf 'CALL'
                  for argument in "$@"; do
                    printf '\t%s' "$argument"
                  done
                  printf '\n'
                } >> "$CODESIGN_LOG"
                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(appBinaryDirectory, "DownKyi"),
                    "fixture",
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(appBinaryDirectory, "libfixture.dylib"),
                    "fixture",
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(appBinaryDirectory, "ManagedDependency.dll"),
                    "fixture",
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(appBinaryDirectory, "runtimeconfig.json"),
                    "{}",
                    cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(appContentsDirectory, "Info.plist"),
                    """
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>CFBundleExecutable</key>
                  <string>DownKyi</string>
                </dict>
                </plist>
                """,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken).ConfigureAwait(false);

                var startInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    WorkingDirectory = fixtureRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(
                    "set -euo pipefail; " +
                    "chmod +x stub-bin/file stub-bin/codesign; " +
                    "export PATH=\"$PWD/stub-bin:$PATH\"; " +
                    "export CODESIGN_LOG=\"$PWD/codesign.log\"; " +
                    $"export MACOS_ADHOC_SIGNING={(adHoc ? "true" : "false")}; " +
                    $"export MACOS_SIGNING_IDENTITY=\"{identity}\"; " +
                    "/bin/bash ./sign.sh Test.app");

                var result = await ExternalProcessTestHarness.RunAsync(
                    startInfo,
                    TimeSpan.FromSeconds(30),
                    ProcessCleanupTimeout,
                    cancellationToken).ConfigureAwait(false);
                Assert.True(
                    result.ExitCode == 0,
                    $"The macOS signing regression fixture failed. stdout={result.StandardOutput} stderr={result.StandardError}");

                calls = (await File.ReadAllLinesAsync(
                        codesignLog,
                        cancellationToken).ConfigureAwait(false))
                    .Select(line => line.Split('\t').Skip(1).ToArray())
                    .ToArray();
            },
            () => DeleteDirectoryAsync(fixtureRoot)).ConfigureAwait(false);

        return calls
               ?? throw new InvalidOperationException("The macOS signing fixture did not produce codesign calls.");
    }

    private static Task DeleteDirectoryAsync(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void AssertCodesignIdentity(string[] arguments, string identity)
    {
        var signIndex = Array.IndexOf(arguments, "--sign");
        Assert.True(signIndex >= 0 && signIndex + 1 < arguments.Length, "codesign must include --sign identity.");
        Assert.Equal(identity, arguments[signIndex + 1]);
    }

    private static void AssertSigningCoverage(string[][] calls)
    {
        Assert.Equal(4, calls.Length);

        var signedPaths = calls.Select(arguments => arguments[^1]).ToArray();
        Assert.Contains("Test.app/Contents/MacOS/DownKyi", signedPaths);
        Assert.Contains("Test.app/Contents/MacOS/libfixture.dylib", signedPaths);
        Assert.Contains("Test.app/Contents/MacOS/ManagedDependency.dll", signedPaths);
        Assert.DoesNotContain("Test.app/Contents/MacOS/runtimeconfig.json", signedPaths);
        Assert.Equal("Test.app/Contents/MacOS/DownKyi", signedPaths[^2]);
        Assert.Equal("Test.app", signedPaths[^1]);
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
