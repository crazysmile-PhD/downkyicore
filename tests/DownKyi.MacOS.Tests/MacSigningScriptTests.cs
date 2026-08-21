using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace DownKyi.MacOS.Tests;

[SupportedOSPlatform("macos")]
public sealed class MacSigningScriptTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void AdHocSigningExecutesUnderSystemBashNounsetWithoutTimestamp()
    {
        var calls = RunSigningFixture(adHoc: true);

        AssertSigningCoverage(calls);
        Assert.All(calls, arguments =>
        {
            Assert.DoesNotContain("--timestamp", arguments);
            AssertCodesignIdentity(arguments, "-");
        });
    }

    [Fact]
    public void DeveloperIdSigningIncludesTimestamp()
    {
        const string identity = "Developer ID Application: DownKyi Test";
        var calls = RunSigningFixture(adHoc: false, identity);

        AssertSigningCoverage(calls);
        Assert.All(calls, arguments =>
        {
            Assert.Contains("--timestamp", arguments);
            AssertCodesignIdentity(arguments, identity);
        });
    }

    private static string[][] RunSigningFixture(
        bool adHoc,
        string identity = "Developer ID Application: DownKyi Test")
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"downkyi-signing-{Guid.NewGuid():N}");
        var stubDirectory = Path.Combine(fixtureRoot, "stub-bin");
        var appContentsDirectory = Path.Combine(fixtureRoot, "Test.app", "Contents");
        var appBinaryDirectory = Path.Combine(appContentsDirectory, "MacOS");
        var codesignLog = Path.Combine(fixtureRoot, "codesign.log");

        Directory.CreateDirectory(stubDirectory);
        Directory.CreateDirectory(appBinaryDirectory);

        try
        {
            foreach (var fileName in new[] { "sign.sh", "codesign-common.sh", "DownKyi.entitlements" })
            {
                var source = File.ReadAllText(
                    Path.Combine(RepositoryRoot, "script", "macos", fileName));
                File.WriteAllText(
                    Path.Combine(fixtureRoot, fileName),
                    source.Replace("\r\n", "\n", StringComparison.Ordinal),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            File.WriteAllText(
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
                """.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.WriteAllText(
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
                """.Replace("\r\n", "\n", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(appBinaryDirectory, "DownKyi"), "fixture");
            File.WriteAllText(Path.Combine(appBinaryDirectory, "libfixture.dylib"), "fixture");
            File.WriteAllText(Path.Combine(appBinaryDirectory, "ManagedDependency.dll"), "fixture");
            File.WriteAllText(Path.Combine(appBinaryDirectory, "runtimeconfig.json"), "{}");
            File.WriteAllText(
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
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(30_000), "The macOS signing regression fixture timed out.");

            var output = standardOutput.GetAwaiter().GetResult();
            var error = standardError.GetAwaiter().GetResult();
            Assert.True(
                process.ExitCode == 0,
                $"The macOS signing regression fixture failed. stdout={output} stderr={error}");

            return File.ReadAllLines(codesignLog)
                .Select(line => line.Split('\t').Skip(1).ToArray())
                .ToArray();
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
        }
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
