using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class ReleaseWorkflowArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ReleaseWorkflowKeepsStrictCrossPlatformGateAndManualPackageValidation()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.Contains("release-gate:", workflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-15", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AnalysisMode=All", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/validate-release-version.ps1", workflow, StringComparison.Ordinal);
        Assert.True(CountOccurrences(workflow, "fail-fast: false") > 0);
        Assert.Equal(3, CountOccurrences(workflow, "validate-publish-output.ps1"));
        Assert.Equal(4, CountOccurrences(workflow, "Get-FileHash"));
    }

    [Fact]
    public void TagReleaseDependencyChainDoesNotStartFromASkippedPullRequestOnlyJob()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.True(
            HasRunnableManifestDetectionDependency(workflow),
            "The manifest-detection dependency must succeed on tag/manual events instead of skipping the release chain.");
    }

    [Fact]
    public void TagReleaseDependencyGuardRejectsJobLevelSkipsAndNonExecutableLookalikes()
    {
        const string validWorkflow = """
            jobs:
              detect-production-manifest-change:
                runs-on: ubuntu-latest
                steps:
                  - name: Detect pull request changes
                    id: filter
                    if: github.event_name == 'pull_request'
                    uses: dorny/paths-filter@v3
            """;
        string[] invalidMutations =
        [
            validWorkflow.Replace(
                "    runs-on: ubuntu-latest",
                "    if: github.event_name == 'pull_request'\n    runs-on: ubuntu-latest",
                StringComparison.Ordinal),
            validWorkflow.Replace(
                "if: github.event_name == 'pull_request'",
                "if: github.event_name != 'pull_request'",
                StringComparison.Ordinal),
            validWorkflow.Replace(
                "uses: dorny/paths-filter@v3",
                "run: echo not-a-diff-detector",
                StringComparison.Ordinal),
            validWorkflow.Replace(
                "uses: dorny/paths-filter@v3",
                "continue-on-error: true\n        uses: dorny/paths-filter@v3",
                StringComparison.Ordinal),
            validWorkflow + """
                  - name: Failing non-PR transition
                    if: github.event_name != 'pull_request'
                    run: exit 1
                """,
            validWorkflow + """
                  - name: Failing multiline non-PR transition
                    if: github.event_name != 'pull_request'
                    run: |
                      echo starting
                      exit 1
                """
        ];

        Assert.True(HasRunnableManifestDetectionDependency(validWorkflow));
        Assert.All(invalidMutations, mutation =>
            Assert.False(HasRunnableManifestDetectionDependency(mutation)));
    }

    [Fact]
    public void ReleaseTagMustMatchTheSingleVersionSource()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-release-version.ps1"));

        Assert.Contains("version.txt", validator, StringComparison.Ordinal);
        Assert.Contains(@"^\d+\.\d+\.\d+$", validator, StringComparison.Ordinal);
        Assert.Contains(
            "$expectedTagRef = \"refs/tags/v$version\"",
            validator,
            StringComparison.Ordinal);
        Assert.Contains(
            "[StringComparison]::Ordinal",
            validator,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublishValidatorRequiresBothMediaToolsAndThePackagedDownloader()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"));

        Assert.Contains("ffmpeg/ffmpeg", validator, StringComparison.Ordinal);
        Assert.Contains("ffmpeg/ffprobe", validator, StringComparison.Ordinal);
        Assert.Contains("aria2/aria2c", validator, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Themes.Fluent", validator, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", validator, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreDoesNotOwnRuntimeSpecificPackageAssets()
    {
        var projectPath = Path.Combine(RepositoryRoot, "DownKyi.Core", "DownKyi.Core.csproj");
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(
            project.Descendants(),
            element => element.Name.LocalName == "RuntimeIdentifier");

        var source = File.ReadAllText(projectPath);
        Assert.DoesNotContain("DownKyiAssetRuntimeIdentifier", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeInformation", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Binary/$(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableOwnsTargetRuntimeAssetSelection()
    {
        var executable = File.ReadAllText(
            Path.Combine(RepositoryRoot, "DownKyi", "DownKyi.csproj"));
        var desktop = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "DownKyi.Desktop",
                "DownKyi.Desktop.csproj"));

        Assert.Contains(
            "<DownKyiAssetRuntimeIdentifier Condition=\"'$(DownKyiAssetRuntimeIdentifier)' == '' And '$(RuntimeIdentifier)' != ''\">$(RuntimeIdentifier)</DownKyiAssetRuntimeIdentifier>",
            executable,
            StringComparison.Ordinal);
        Assert.Contains("RuntimeInformation", executable, StringComparison.Ordinal);
        Assert.Contains(
            @"..\DownKyi.Core\Binary\$(DownKyiAssetRuntimeIdentifier)\aria2\*",
            executable,
            StringComparison.Ordinal);
        Assert.Contains(
            @"..\DownKyi.Core\Binary\$(DownKyiAssetRuntimeIdentifier)\ffmpeg\*",
            executable,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopyToPublishDirectory=\"PreserveNewest\"",
            executable,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AdditionalProperties=\"DownKyiAssetRuntimeIdentifier=",
            executable,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DownKyiAssetRuntimeIdentifier",
            desktop,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalAssetManifestPinsImmutableReleaseUrlsAndSha256Digests()
    {
        var manifestPath = Path.Combine(
            RepositoryRoot,
            "script",
            "assets",
            "external-assets.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var aria2 = manifest.RootElement.GetProperty("aria2");
        var sourceCommit = Assert.IsType<string>(
            aria2.GetProperty("sourceCommit").GetString());
        var sourceTag = Assert.IsType<string>(
            aria2.GetProperty("sourceTag").GetString());
        var version = Assert.IsType<string>(
            aria2.GetProperty("version").GetString());
        Assert.Matches(
            "^[0-9a-f]{40}$",
            sourceCommit);
        Assert.Equal(version, sourceTag);

        foreach (var tool in manifest.RootElement.EnumerateObject())
        {
            foreach (var asset in tool.Value.GetProperty("assets").EnumerateObject())
            {
                AssertPinnedAsset(asset.Value, "url", "sha256");

                if (string.Equals(tool.Name, "aria2", StringComparison.Ordinal))
                {
                    var binaryChecksum = asset.Value.GetProperty("binarySha256").GetString();
                    Assert.NotNull(binaryChecksum);
                    Assert.Matches("^[a-f0-9]{64}$", binaryChecksum);
                }

                if (asset.Value.TryGetProperty("ffprobeUrl", out _))
                {
                    AssertPinnedAsset(asset.Value, "ffprobeUrl", "ffprobeSha256");
                }
            }
        }
    }

    [Fact]
    public void ExternalAssetScriptsResolveFromTheirOwnDirectoryAndShareTheManifest()
    {
        var scripts = new[]
        {
            File.ReadAllText(Path.Combine(RepositoryRoot, "script", "ffmpeg.ps1")),
            File.ReadAllText(Path.Combine(RepositoryRoot, "script", "aria2.ps1")),
            File.ReadAllText(Path.Combine(RepositoryRoot, "script", "ffmpeg.sh")),
            File.ReadAllText(Path.Combine(RepositoryRoot, "script", "aria2.sh")),
        };

        Assert.All(scripts, source =>
        {
            Assert.Contains("external-assets.json", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "download_dir=\"./downloads\"",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Create-Dir \".\\downloads\"",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("curl -k", source, StringComparison.Ordinal);
            Assert.DoesNotContain("curl --insecure", source, StringComparison.Ordinal);
        });
        Assert.Contains("$PSScriptRoot", scripts[0], StringComparison.Ordinal);
        Assert.Contains("$PSScriptRoot", scripts[1], StringComparison.Ordinal);
        Assert.Contains("BASH_SOURCE[0]", scripts[2], StringComparison.Ordinal);
        Assert.Contains("BASH_SOURCE[0]", scripts[3], StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsExternalAssetInstallersUseTheSharedBoundedRetryOwner()
    {
        var aria2Installer = File.ReadAllText(Path.Combine(RepositoryRoot, "script", "aria2.ps1"));
        var ffmpegInstaller = File.ReadAllText(Path.Combine(RepositoryRoot, "script", "ffmpeg.ps1"));

        Assert.All(new[] { aria2Installer, ffmpegInstaller }, source =>
        {
            Assert.Contains("download-external-asset.ps1", source, StringComparison.Ordinal);
            Assert.Contains("Invoke-ExternalAssetDownload", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-BitsTransfer", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExternalAssetDownloadRetriesTransientFailuresAndFailsClosed()
    {
        var helperPath = Path.Combine(RepositoryRoot, "script", "download-external-asset.ps1");
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            . '{{helperPath.Replace("'", "''", StringComparison.Ordinal)}}'
            $successPath = [IO.Path]::GetTempFileName()
            $failurePath = [IO.Path]::GetTempFileName()
            try {
                $successAttempts = 0
                Invoke-ExternalAssetDownload `
                    -Uri 'https://example.invalid/immutable.zip' `
                    -Destination $successPath `
                    -MaximumAttempts 3 `
                    -RetryDelaySeconds 0 `
                    -TransferOperation {
                        param($source, $destination)
                        $script:successAttempts++
                        if ($script:successAttempts -lt 3) {
                            throw [IO.IOException]::new('injected transient transport failure')
                        }
                        [IO.File]::WriteAllText($destination, 'verified later by the manifest checksum')
                    }
                if ($successAttempts -ne 3 -or -not (Test-Path -LiteralPath $successPath)) {
                    throw 'The bounded retry did not recover on the final allowed attempt.'
                }

                $failureAttempts = 0
                $failedClosed = $false
                try {
                    Invoke-ExternalAssetDownload `
                        -Uri 'https://example.invalid/immutable.zip' `
                        -Destination $failurePath `
                        -MaximumAttempts 3 `
                        -RetryDelaySeconds 0 `
                        -TransferOperation {
                            param($source, $destination)
                            $script:failureAttempts++
                            [IO.File]::WriteAllText($destination, 'injected partial response')
                            throw [IO.IOException]::new('injected persistent transport failure')
                        }
                }
                catch [IO.IOException] {
                    $failedClosed = $true
                }
                if (-not $failedClosed -or $failureAttempts -ne 3) {
                    throw 'Retry exhaustion did not preserve the transport failure.'
                }
                if (Test-Path -LiteralPath $failurePath) {
                    throw 'Retry exhaustion left an unverified partial asset behind.'
                }
            }
            finally {
                foreach ($path in @($successPath, $failurePath)) {
                    if (Test-Path -LiteralPath $path) {
                        Remove-Item -LiteralPath $path -Force
                    }
                }
            }
            exit 0
            """;

        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            ArgumentList =
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                encodedCommand
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(30_000), "The external-asset retry regression timed out.");

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            $"External-asset retry regression failed. stdout={standardOutput} stderr={standardError}");
    }

    [Fact]
    public void MacPackageBuildRestoresTheRequestedRuntimeBeforePublishing()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        Assert.Contains(
            "dotnet restore DownKyi/DownKyi.csproj -r osx-${{ matrix.cpu }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet publish DownKyi/DownKyi.csproj --no-restore --self-contained -r osx-${{ matrix.cpu }}",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacReleaseAdHocSignsAndVerifiesFinalArtifacts()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var signScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "sign.sh"));
        var codesignCommonScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "codesign-common.sh"));
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "package.sh"));
        var prepareAppLayoutScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "prepare-app-layout.sh"));
        var verifyAppScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-app.sh"));
        var verifyAppLaunchScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-app-launch.sh"));
        var verifyDmgScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-dmg.sh"));

        Assert.DoesNotContain(
            "MACOS_SIGNING_REQUIRED",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Require macOS signing credentials for release",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Formal macOS releases require MACOS_CERTIFICATE, MACOS_CERTIFICATE_PWD, APPLE_ID, TEAM_ID, and APP_SPECIFIC_PASSWORD.",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("Resolve signing identity", workflow, StringComparison.Ordinal);
        Assert.Contains("MACOS_ADHOC_SIGNING: ${{ env.HAS_MACOS_SIGNING != 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("os: macos-15-intel", workflow, StringComparison.Ordinal);
        Assert.Contains("os: macos-15", workflow, StringComparison.Ordinal);
        Assert.Contains("Run macOS packaging regressions", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify packaged DMG contents and launch app", workflow, StringComparison.Ordinal);

        AssertInOrder(
            workflow,
            "Package app",
            "Validate packaged runtime",
            "Sign app",
            "Verify app signature",
            "Notarize app",
            "Verify notarized app",
            "Create DMG",
            "Sign DMG",
            "Verify signed DMG",
            "Notarize DMG",
            "Verify notarized DMG",
            "Hash DMG",
            "Upload build artifacts");

        Assert.DoesNotContain("biao yao", signScript, StringComparison.Ordinal);
        Assert.DoesNotContain("codesign --deep --force", signScript, StringComparison.Ordinal);
        Assert.Contains("resolve_signing_identity", signScript, StringComparison.Ordinal);
        Assert.Contains("find \"$APP_NAME/Contents\" -type f", signScript, StringComparison.Ordinal);
        Assert.Contains("is_signable_app_file \"$file\"", signScript, StringComparison.Ordinal);
        Assert.Contains("codesign_app_path \"$file\"", signScript, StringComparison.Ordinal);
        Assert.Contains("Print :CFBundleExecutable", signScript, StringComparison.Ordinal);
        Assert.Contains("codesign_app_path \"$MAIN_EXECUTABLE\"", signScript, StringComparison.Ordinal);
        Assert.Contains("codesign_app_path \"$APP_NAME\"", signScript, StringComparison.Ordinal);
        Assert.DoesNotContain("CODESIGN_TIMESTAMP_ARGS", signScript, StringComparison.Ordinal);
        Assert.Contains("is_signable_app_file()", codesignCommonScript, StringComparison.Ordinal);
        Assert.Contains("*.dll|*.exe)", codesignCommonScript, StringComparison.Ordinal);
        Assert.Contains("file \"$path\" | grep -q \"Mach-O\"", codesignCommonScript, StringComparison.Ordinal);
        Assert.Contains("/bin/bash ./prepare-app-layout.sh \"$APP_NAME\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("is_signable_app_file \"$path\"", prepareAppLayoutScript, StringComparison.Ordinal);
        Assert.Contains("Contents/Resources/dotnet", prepareAppLayoutScript, StringComparison.Ordinal);
        Assert.Contains("ln -s", prepareAppLayoutScript, StringComparison.Ordinal);

        Assert.Contains("codesign --verify --deep --strict --verbose=2", verifyAppScript, StringComparison.Ordinal);
        Assert.Contains("spctl --assess --type execute", verifyAppScript, StringComparison.Ordinal);
        Assert.Contains("kill -TERM \"$PID\"", verifyAppLaunchScript, StringComparison.Ordinal);
        Assert.Contains("kill -KILL \"$PID\"", verifyAppLaunchScript, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --verbose=2", verifyDmgScript, StringComparison.Ordinal);
        Assert.Contains("xcrun stapler validate", verifyDmgScript, StringComparison.Ordinal);
        Assert.Contains("spctl --assess --type open --context context:primary-signature", verifyDmgScript, StringComparison.Ordinal);
    }

    [Fact]
    public void V112RecoverySeparatesControlPlaneFromImmutableReleaseSubject()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "release-v112-recovery.yml"));
        var subjectValidator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-v112-recovery-subject.ps1"));
        var artifactValidator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-v112-release-artifacts.ps1"));

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.Contains("path: tooling", workflow, StringComparison.Ordinal);
        Assert.Contains("path: subject", workflow, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ inputs.subject_sha }}", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish ./subject/DownKyi/DownKyi.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("working-directory: subject", workflow, StringComparison.Ordinal);
        Assert.Contains("Resolve macOS release trust mode", workflow, StringComparison.Ordinal);
        Assert.Contains("macos_trust_mode: ${{ steps.macos_trust.outputs.macos_trust_mode }}", workflow, StringComparison.Ordinal);
        Assert.Contains("HAS_MACOS_SIGNING: ${{ needs.authority.outputs.has_macos_signing }}", workflow, StringComparison.Ordinal);
        Assert.Contains("MACOS_ADHOC_SIGNING: ${{ env.HAS_MACOS_SIGNING != 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ env.HAS_MACOS_SIGNING == 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("Strictly verify final app", workflow, StringComparison.Ordinal);
        Assert.Contains("./verify-dmg-contents.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("Render release notes for selected trust mode", workflow, StringComparison.Ordinal);
        Assert.Contains("bodyFile: tooling/artifacts/v1.1.2-release-notes.md", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Require Apple credentials for formal publish", workflow, StringComparison.Ordinal);

        var macSteps = GetWorkflowSteps(workflow, "  build-macos:");
        AssertStepCondition(macSteps, "Import Apple certificate", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Resolve Developer ID identity", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Notarize and verify app", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Sign, notarize, and verify DMG", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepHasNoCondition(macSteps, "Strictly verify final app");
        AssertStepHasNoCondition(macSteps, "Remount DMG, strictly verify, and launch app");
        Assert.Contains("tag: v1.1.2", workflow, StringComparison.Ordinal);
        Assert.Contains("commit: 16c690d8719f86eb6eecb56c24efabc1afc41d55", workflow, StringComparison.Ordinal);
        Assert.Contains("prerelease: false", workflow, StringComparison.Ordinal);
        Assert.Contains("makeLatest: true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("git tag", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push --force", workflow, StringComparison.Ordinal);

        Assert.Contains("$expectedReleaseVersion = 'v1.1.2'", subjectValidator, StringComparison.Ordinal);
        Assert.Contains("$expectedSubjectSha = '16c690d8719f86eb6eecb56c24efabc1afc41d55'", subjectValidator, StringComparison.Ordinal);
        Assert.Contains("cat-file -t $expectedReleaseVersion", subjectValidator, StringComparison.Ordinal);
        Assert.Contains("status --porcelain --untracked-files=no", subjectValidator, StringComparison.Ordinal);
        Assert.Contains("Validated $($expected.Count) v1.1.2 packages", artifactValidator, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", artifactValidator, StringComparison.Ordinal);
        Assert.Contains("Publish manifest contract failed", artifactValidator, StringComparison.Ordinal);
    }

    [Fact]
    public void V112MacosTrustResolverRequiresZeroOrAllCredentials()
    {
        var script = Path.Combine(RepositoryRoot, "script", "resolve-v112-macos-trust.ps1");
        var outputPath = Path.GetTempFileName();

        try
        {
            var adHoc = RunPowerShellScript(
                script,
                ["-OutputPath", outputPath],
                new Dictionary<string, string>());
            Assert.Equal(0, adHoc.ExitCode);
            Assert.Contains("ad-hoc", File.ReadAllText(outputPath), StringComparison.Ordinal);
            Assert.DoesNotContain("developer-id", File.ReadAllText(outputPath), StringComparison.Ordinal);

            IReadOnlyDictionary<string, string> developerIdEnvironment = new Dictionary<string, string>
            {
                ["MACOS_CERTIFICATE"] = "fixture-certificate",
                ["MACOS_CERTIFICATE_PWD"] = "fixture-password",
                ["APPLE_ID"] = "fixture@example.invalid",
                ["TEAM_ID"] = "FIXTURETEAM",
                ["APP_SPECIFIC_PASSWORD"] = "fixture-app-password"
            };
            var developerId = RunPowerShellScript(
                script,
                ["-OutputPath", outputPath],
                developerIdEnvironment);
            Assert.Equal(0, developerId.ExitCode);
            Assert.Contains("developer-id", File.ReadAllText(outputPath), StringComparison.Ordinal);

            var partial = RunPowerShellScript(
                script,
                ["-OutputPath", outputPath],
                new Dictionary<string, string> { ["APPLE_ID"] = "fixture@example.invalid" });
            Assert.NotEqual(0, partial.ExitCode);
            Assert.Contains("Partial Apple credentials", partial.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void V112RecoveryReleaseNotesDiscloseSelectedTrustMode()
    {
        var script = Path.Combine(RepositoryRoot, "script", "render-v112-recovery-release-notes.ps1");
        var outputPath = Path.GetTempFileName();

        try
        {
            var adHoc = RunPowerShellScript(
                script,
                ["-TrustMode", "ad-hoc", "-OutputPath", outputPath],
                new Dictionary<string, string>());
            Assert.Equal(0, adHoc.ExitCode);
            var adHocNotes = File.ReadAllText(outputPath);
            Assert.Contains("ad-hoc identity", adHocNotes, StringComparison.Ordinal);
            Assert.Contains("not notarized", adHocNotes, StringComparison.Ordinal);
            Assert.Contains("does not have Gatekeeper distribution trust", adHocNotes, StringComparison.Ordinal);

            var developerId = RunPowerShellScript(
                script,
                ["-TrustMode", "developer-id", "-OutputPath", outputPath],
                new Dictionary<string, string>());
            Assert.Equal(0, developerId.ExitCode);
            var developerIdNotes = File.ReadAllText(outputPath);
            Assert.Contains("Developer ID", developerIdNotes, StringComparison.Ordinal);
            Assert.Contains("notarization", developerIdNotes, StringComparison.Ordinal);
            Assert.Contains("stapling", developerIdNotes, StringComparison.Ordinal);
            Assert.DoesNotContain("not notarized", developerIdNotes, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void VersionFileIsTheOnlyProjectVersionSourceAndControlsAssemblyMetadata()
    {
        var versionText = File.ReadAllText(Path.Combine(RepositoryRoot, "version.txt")).Trim();
        var expected = Version.Parse(versionText);
        var expectedAssemblyVersion = new Version(
            expected.Major,
            expected.Minor,
            expected.Build,
            0);
        var props = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains(
            "System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)version.txt').Trim()",
            props,
            StringComparison.Ordinal);

        var projectVersionElements = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName is
                    "Version" or
                    "VersionPrefix" or
                    "AssemblyVersion" or
                    "FileVersion" or
                    "InformationalVersion")
                .Select(element => $"{Path.GetRelativePath(RepositoryRoot, path)} -> {element.Name.LocalName}"))
            .ToArray();

        Assert.Empty(projectVersionElements);

        var assembly = typeof(ReleaseWorkflowArchitectureTests).Assembly;
        Assert.Equal(expectedAssemblyVersion, assembly.GetName().Version);

        var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
        Assert.Equal(expectedAssemblyVersion.ToString(), fileVersion);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Assert.StartsWith(versionText, informationalVersion, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
    }

    private static PowerShellResult RunPowerShellScript(
        string script,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string[] credentialNames =
        [
            "MACOS_CERTIFICATE",
            "MACOS_CERTIFICATE_PWD",
            "APPLE_ID",
            "TEAM_ID",
            "APP_SPECIFIC_PASSWORD"
        ];
        foreach (var name in credentialNames)
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), $"Release trust regression timed out: {script}");
        return new PowerShellResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record PowerShellResult(int ExitCode, string StandardOutput, string StandardError);

    private static void AssertInOrder(string source, params string[] fragments)
    {
        var previousIndex = -1;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{fragment}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static bool HasRunnableManifestDetectionDependency(string workflow)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var job = GetYamlBlock(lines, "  detect-production-manifest-change:", 2);
        if (job.Count == 0 || HasYamlKeyPrefix(job, 4, "if:"))
        {
            return false;
        }

        var stepsStart = job.FindIndex(line =>
            GetIndent(line) == 4 && string.Equals(line.Trim(), "steps:", StringComparison.Ordinal));
        if (stepsStart < 0)
        {
            return false;
        }

        var steps = GetYamlSequenceBlocks(job[(stepsStart + 1)..], 6);
        var pullRequestDetector = steps.Any(step =>
            HasExactIf(step, "github.event_name == 'pull_request'") &&
            HasYamlKey(step, 8, "id: filter") &&
            HasYamlValuePrefix(step, 8, "uses:", "dorny/paths-filter@") &&
            !HasYamlKey(step, 8, "continue-on-error: true"));

        return pullRequestDetector && steps.Count == 1;
    }

    private static List<List<string>> GetWorkflowSteps(string workflow, string jobHeader)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var job = GetYamlBlock(lines, jobHeader, 2);
        var stepsStart = job.FindIndex(line =>
            GetIndent(line) == 4 && string.Equals(line.Trim(), "steps:", StringComparison.Ordinal));
        Assert.True(stepsStart >= 0, $"Workflow job {jobHeader.Trim()} has no steps.");
        return GetYamlSequenceBlocks(job[(stepsStart + 1)..], 6);
    }

    private static void AssertStepCondition(
        IReadOnlyList<List<string>> steps,
        string stepName,
        string expectedCondition)
    {
        var step = FindWorkflowStep(steps, stepName);
        Assert.Contains(
            step,
            line => GetIndent(line) == 8 &&
                    string.Equals(line.Trim(), $"if: {expectedCondition}", StringComparison.Ordinal));
    }

    private static void AssertStepHasNoCondition(
        IReadOnlyList<List<string>> steps,
        string stepName)
    {
        var step = FindWorkflowStep(steps, stepName);
        Assert.DoesNotContain(
            step,
            line => GetIndent(line) == 8 && line.Trim().StartsWith("if:", StringComparison.Ordinal));
    }

    private static List<string> FindWorkflowStep(
        IReadOnlyList<List<string>> steps,
        string stepName)
    {
        var step = steps.SingleOrDefault(candidate => candidate.Any(line =>
            GetIndent(line) == 6 &&
            string.Equals(line.Trim(), $"- name: {stepName}", StringComparison.Ordinal)));
        Assert.NotNull(step);
        return step;
    }

    private static List<string> GetYamlBlock(
        string[] lines,
        string header,
        int headerIndent)
    {
        var start = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.Equals(lines[index], header, StringComparison.Ordinal))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line) &&
                !line.TrimStart().StartsWith('#') &&
                GetIndent(line) <= headerIndent)
            {
                break;
            }

            result.Add(line);
        }

        return result;
    }

    private static List<List<string>> GetYamlSequenceBlocks(
        IReadOnlyList<string> lines,
        int itemIndent)
    {
        var result = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line) && GetIndent(line) < itemIndent)
            {
                break;
            }

            if (GetIndent(line) == itemIndent && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                current = [];
                result.Add(current);
            }

            current?.Add(line);
        }

        return result;
    }

    private static bool HasExactIf(IReadOnlyList<string> block, string expression)
    {
        return block.Any(line =>
            GetIndent(line) == 8 &&
            string.Equals(line.Trim(), $"if: {expression}", StringComparison.Ordinal));
    }

    private static bool HasYamlKey(IReadOnlyList<string> block, int indent, string key)
    {
        return block.Any(line =>
            GetIndent(line) == indent && string.Equals(line.Trim(), key, StringComparison.Ordinal));
    }

    private static bool HasYamlKeyPrefix(IReadOnlyList<string> block, int indent, string key)
    {
        return block.Any(line =>
            GetIndent(line) == indent && line.Trim().StartsWith(key, StringComparison.Ordinal));
    }

    private static bool HasYamlValuePrefix(
        IReadOnlyList<string> block,
        int indent,
        string key,
        string valuePrefix)
    {
        return block.Any(line =>
            GetIndent(line) == indent &&
            line.Trim().StartsWith($"{key} {valuePrefix}", StringComparison.Ordinal));
    }

    private static int GetIndent(string line)
    {
        return line.Length - line.TrimStart().Length;
    }

    private static void AssertPinnedAsset(
        JsonElement asset,
        string urlProperty,
        string checksumProperty)
    {
        var url = asset.GetProperty(urlProperty).GetString();
        var checksum = asset.GetProperty(checksumProperty).GetString();

        Assert.NotNull(url);
        Assert.NotNull(checksum);
        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.DoesNotContain("/latest/", url, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[a-f0-9]{64}$", checksum);
    }

    private static bool IsBuildOutput(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
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
