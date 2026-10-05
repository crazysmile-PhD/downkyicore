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
        Assert.Contains("vars.UBUNTU_X64_RUNNER", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-15", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:AnalysisMode=All", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/test-solution.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/validate-release-version.ps1", workflow, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(workflow, "fail-fast: false"));
        Assert.Equal(3, CountOccurrences(workflow, "validate-publish-output.ps1"));
        Assert.Equal(5, CountOccurrences(workflow, "Get-FileHash"));
    }

    [Fact]
    public void PullRequestPackageUploadsCannotOverrideValidationResults()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));

        AssertStepContinueOnError(
            GetWorkflowSteps(workflow, "  release-gate:"),
            "Upload test results and failure diagnostics",
            "${{ github.event_name == 'pull_request' }}");
        AssertStepContinueOnError(
            GetWorkflowSteps(workflow, "  build-windows:"),
            "Upload build artifacts ${{ matrix.kind }}",
            "${{ github.event_name == 'pull_request' }}");

        var linuxSteps = GetWorkflowSteps(workflow, "  build-linux:");
        AssertStepHasNoCondition(linuxSteps, "Install mirrored AppImage packaging assets");
        Assert.DoesNotContain(
            FindWorkflowStep(linuxSteps, "Install mirrored AppImage packaging assets"),
            line => line.Contains("matrix.cpu", StringComparison.Ordinal));
        AssertStepContinueOnError(
            linuxSteps,
            "Upload build artifacts ${{ matrix.kind }}",
            "${{ github.event_name == 'pull_request' }}");
        AssertStepContinueOnError(
            linuxSteps,
            "Upload permission-preserving AppImage transport",
            "${{ github.event_name == 'pull_request' }}");
        AssertStepHasNoContinueOnError(
            linuxSteps,
            "Upload ARM64 native-validation candidate transport");
        AssertStepHasNoContinueOnError(
            GetWorkflowSteps(workflow, "  build-linux-publish:"),
            "Upload canonical Linux publish transport");

        var arm64Steps = GetWorkflowSteps(workflow, "  validate-linux-arm64:");
        AssertStepContinueOnError(
            arm64Steps,
            "Upload validated ARM64 AppImage transport",
            "${{ github.event_name == 'pull_request' }}");
        AssertStepContinueOnError(
            arm64Steps,
            "Upload validated ARM64 Debian package",
            "${{ github.event_name == 'pull_request' }}");

        var macSteps = GetWorkflowSteps(workflow, "  build-macos:");
        AssertStepContinueOnError(
            macSteps,
            "Upload macOS packaging test results and failure diagnostics",
            "${{ github.event_name == 'pull_request' }}");
        AssertStepContinueOnError(
            macSteps,
            "Upload build artifacts",
            "${{ github.event_name == 'pull_request' }}");
    }

    [Fact]
    public void SolutionBuildConsumersUseCompletedImplementationAssemblies()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.True(HasSafeReferenceAssemblyConsumerPolicy(props));

        var enabledMutation = new XDocument(props);
        enabledMutation
            .Descendants()
            .Single(element => element.Name.LocalName == "CompileUsingReferenceAssemblies")
            .Value = "true";
        Assert.False(HasSafeReferenceAssemblyConsumerPolicy(enabledMutation));

        var conditionalMutation = new XDocument(props);
        conditionalMutation
            .Descendants()
            .Single(element => element.Name.LocalName == "CompileUsingReferenceAssemblies")
            .SetAttributeValue("Condition", "'$(OS)' == 'Windows_NT'");
        Assert.False(HasSafeReferenceAssemblyConsumerPolicy(conditionalMutation));
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
    public void BuildPullRequestTriggerIsRestrictedToExternalAssetOwners()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var pullRequest = GetYamlBlock(lines, "  pull_request:", 2);
        var pathBlock = GetYamlBlock(pullRequest.ToArray(), "    paths:", 4);
        var triggerPaths = pathBlock
            .Where(line => GetIndent(line) == 6 && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.Trim()[2..].Trim('\'', '"'))
            .ToArray();
        var tooling = GetYamlBlock(lines, "  ffmpeg-tooling:", 2);
        var preflight = GetYamlBlock(lines, "  external-assets-preflight:", 2);
        var gate = GetYamlBlock(lines, "  ffmpeg-required-gate:", 2);
        var release = GetYamlBlock(lines, "  release-gate:", 2);

        Assert.Equal(
            [
                ".github/workflows/build.yml",
                ".github/workflows/update-ffmpeg-assets.yml",
                "script/assets/external-assets.json",
                "script/download-external-asset.ps1",
                "script/install-appimage-assets.ps1",
                "script/ffmpeg-assets.py",
                "script/ffmpeg.ps1",
                "script/ffmpeg.sh",
                "script/tests/test_ffmpeg_assets.py"
            ],
            triggerPaths);
        Assert.DoesNotContain("needs: detect-production-manifest-change", tooling);
        Assert.DoesNotContain("github.event_name != 'pull_request'", tooling);
        Assert.Contains("    if: ${{ always() && !inputs.update_ffmpeg_assets && needs.ffmpeg-tooling.result == 'success' && (github.event_name != 'pull_request' || needs.detect-production-manifest-change.outputs.external_assets == 'true') }}", preflight);
        Assert.Contains("    needs: external-assets-preflight", release);
        Assert.Contains("    name: FFmpeg manifest gate", gate);
        Assert.Contains("    if: ${{ always() && github.event_name == 'pull_request' }}", gate);
        Assert.Contains("      - detect-production-manifest-change", gate);
        Assert.Contains("      - ffmpeg-tooling", gate);
        Assert.Contains("      - external-assets-preflight", gate);
        Assert.Contains("          test \"$DETECTION_RESULT\" = success", gate);
        Assert.Contains("          test \"$TOOLING_RESULT\" = success", gate);
        Assert.Contains("          if [ \"$EXTERNAL_ASSETS\" = true ]; then", gate);
        Assert.Contains("            test \"$PREFLIGHT_RESULT\" = skipped", gate);
        Assert.DoesNotContain("src/DownKyi.Desktop/**", triggerPaths);
    }

    [Fact]
    public void FfmpegPullRequestValidationFailuresReachTheRequiredCheck()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var detector = GetYamlBlock(lines, "  detect-production-manifest-change:", 2);
        var tooling = GetYamlBlock(lines, "  ffmpeg-tooling:", 2);
        var preflight = GetYamlBlock(lines, "  external-assets-preflight:", 2);
        var gate = GetYamlBlock(lines, "  ffmpeg-required-gate:", 2);

        Assert.Contains("              - 'script/assets/external-assets.json'", detector);
        Assert.Contains("              - 'script/download-external-asset.ps1'", detector);
        Assert.Contains("              - 'script/install-appimage-assets.ps1'", detector);
        Assert.DoesNotContain("ffmpeg_related:", detector);
        Assert.DoesNotContain("    needs: detect-production-manifest-change", tooling);
        Assert.Contains("      - name: Run FFmpeg asset guards", tooling);
        Assert.Contains("        run: python -m unittest script/tests/test_ffmpeg_assets.py -v", tooling);
        Assert.DoesNotContain("continue-on-error: true", tooling);
        Assert.Contains("        run: python script/ffmpeg-assets.py validate-manifest --manifest script/assets/external-assets.json", preflight);
        Assert.Contains("        run: python script/ffmpeg-assets.py preflight --manifest script/assets/external-assets.json --timeout 30", preflight);
        Assert.Contains("        run: ./script/install-appimage-assets.ps1 -ToolPath '${{ runner.temp }}/appimage-assets-preflight'", preflight);
        Assert.Contains("          set -e", gate);
        Assert.DoesNotContain("FFMPEG_RELATED", gate);
        Assert.Contains("          EXTERNAL_ASSETS: ${{ needs.detect-production-manifest-change.outputs.external_assets }}", gate);
        Assert.Contains("          test \"$TOOLING_RESULT\" = success", gate);
        Assert.Contains("            test \"$PREFLIGHT_RESULT\" = success", gate);
        Assert.DoesNotContain("continue-on-error: true", gate);
    }

    [Fact]
    public void TagReleaseDependencyGuardRejectsJobLevelSkipsAndNonExecutableLookalikes()
    {
        const string validWorkflow = """
            jobs:
              detect-production-manifest-change:
                runs-on: ${{ vars.UBUNTU_X64_RUNNER }}
                steps:
                  - name: Detect pull request changes
                    id: filter
                    if: github.event_name == 'pull_request'
                    uses: dorny/paths-filter@v3
            """;
        string[] invalidMutations =
        [
            validWorkflow.Replace(
                "    runs-on: ${{ vars.UBUNTU_X64_RUNNER }}",
                "    if: github.event_name == 'pull_request'\n    runs-on: ${{ vars.UBUNTU_X64_RUNNER }}",
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
            $installRoot = Join-Path ([IO.Path]::GetTempPath()) ('verified-asset-' + [Guid]::NewGuid().ToString('N'))
            $stagingPath = Join-Path $installRoot 'staging'
            $verifiedDestination = Join-Path $installRoot 'formal/verified.bin'
            $rejectedDestination = Join-Path $installRoot 'formal/rejected.bin'
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

                $script:verifiedBytes = [Text.Encoding]::UTF8.GetBytes('verified')
                $script:rejectedBytes = [Text.Encoding]::UTF8.GetBytes('tampered')
                $verifiedSha = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($script:verifiedBytes)).ToLowerInvariant()
                Install-VerifiedExternalAsset `
                    -Uri 'https://example.invalid/verified.bin' `
                    -Destination $verifiedDestination `
                    -StagingDirectory $stagingPath `
                    -Sha256 $verifiedSha `
                    -ExpectedSize $script:verifiedBytes.Length `
                    -MaximumAttempts 1 `
                    -RetryDelaySeconds 0 `
                    -TransferOperation {
                        param($source, $destination)
                        [IO.File]::WriteAllBytes($destination, $script:verifiedBytes)
                    }
                if (-not (Test-Path -LiteralPath $verifiedDestination -PathType Leaf)) {
                    throw 'A verified external asset was not committed to its formal destination.'
                }

                $checksumRejected = $false
                try {
                    Install-VerifiedExternalAsset `
                        -Uri 'https://example.invalid/rejected.bin' `
                        -Destination $rejectedDestination `
                        -StagingDirectory $stagingPath `
                        -Sha256 $verifiedSha `
                        -ExpectedSize $script:rejectedBytes.Length `
                        -MaximumAttempts 1 `
                        -RetryDelaySeconds 0 `
                        -TransferOperation {
                            param($source, $destination)
                            [IO.File]::WriteAllBytes($destination, $script:rejectedBytes)
                        }
                }
                catch [IO.InvalidDataException] {
                    $checksumRejected = $true
                }
                if (-not $checksumRejected) {
                    throw 'A checksum mismatch was not rejected.'
                }
                if (Test-Path -LiteralPath $rejectedDestination) {
                    throw 'A rejected external asset reached its formal destination.'
                }
                if (@(Get-ChildItem -LiteralPath $stagingPath -Force).Count -ne 0) {
                    throw 'A rejected external asset remained in staging.'
                }
            }
            finally {
                foreach ($path in @($successPath, $failurePath)) {
                    if (Test-Path -LiteralPath $path) {
                        Remove-Item -LiteralPath $path -Force
                    }
                }
                if (Test-Path -LiteralPath $verifiedDestination) {
                    Remove-Item -LiteralPath $verifiedDestination -Force
                }
                foreach ($directory in @(
                    $stagingPath,
                    (Join-Path $installRoot 'formal'),
                    $installRoot)) {
                    if (Test-Path -LiteralPath $directory) {
                        Remove-Item -LiteralPath $directory -Force
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
        process.WaitForExit();

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
        var verifyAppSignatureScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-app-signature.sh"));
        var verifyAppLoaderScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-app-loader.sh"));
        var verifyAppBundleLaunchSource = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-app-bundle-launch.swift"));
        var verifyDmgScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-dmg.sh"));
        var validateDmgPackageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "validate-dmg-package.sh"));
        var ariaIntegrityScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "aria2-runtime-integrity.sh"));
        var ariaReadinessScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "verify-aria2-rpc-readiness.sh"));

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
        Assert.Contains("Validate mounted and installed macOS package contracts", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Verify pre-sign aria2 supply-chain boundary", workflow, StringComparison.Ordinal);

        AssertInOrder(
            workflow,
            "Package app",
            "Validate packaged runtime",
            "Sign app",
            "Verify signed app trust",
            "Verify signed aria2 integrity",
            "Notarize app",
            "Verify notarized app trust",
            "Create DMG",
            "Sign DMG",
            "Verify signed DMG",
            "Notarize DMG",
            "Verify notarized DMG",
            "Validate mounted and installed macOS package contracts",
            "Hash DMG",
            "Upload build artifacts");

        Assert.DoesNotContain("biao yao", signScript, StringComparison.Ordinal);
        Assert.DoesNotContain("codesign --deep --force", signScript, StringComparison.Ordinal);
        Assert.Contains("resolve_signing_identity", signScript, StringComparison.Ordinal);
        Assert.Contains("find \"$APP_NAME/Contents\" -type f", signScript, StringComparison.Ordinal);
        Assert.Contains("is_signable_app_file \"$file\"", signScript, StringComparison.Ordinal);
        Assert.Contains("codesign_app_path \"$file\"", signScript, StringComparison.Ordinal);
        AssertInOrder(
            signScript,
            "aria2-runtime-integrity.sh\" verify \"$APP_NAME\"",
            "codesign_app_path \"$file\"",
            "aria2-runtime-integrity.sh\" refresh \"$APP_NAME\"",
            "codesign_app_path \"$MAIN_EXECUTABLE\"",
            "codesign_app_path \"$APP_NAME\"");
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

        Assert.Contains("codesign --verify --deep --strict --verbose=2", verifyAppSignatureScript, StringComparison.Ordinal);
        Assert.Contains("spctl --assess --type execute", verifyAppSignatureScript, StringComparison.Ordinal);
        Assert.Contains("Print :CFBundleExecutable", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.Contains("libcoreclr.dylib", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.Contains("/usr/sbin/lsof", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.Contains("kill -KILL \"$PID\"", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.DoesNotContain("events.jsonl", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.DoesNotContain("LAUNCH_SECONDS", verifyAppLoaderScript, StringComparison.Ordinal);
        Assert.Contains("NSWorkspace.shared.openApplication", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("isFinishedLaunching", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("isTerminated", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("RunLoop.current.run", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("application.isFinishedLaunching", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("application.isTerminated", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("createsNewApplicationInstance = true", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("allowsRunningApplicationSubstitution = false", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("events.jsonl", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LAUNCH_SECONDS", verifyAppBundleLaunchSource, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --verbose=2", verifyDmgScript, StringComparison.Ordinal);
        Assert.Contains("xcrun stapler validate", verifyDmgScript, StringComparison.Ordinal);
        Assert.Contains("spctl --assess --type open --context context:primary-signature", verifyDmgScript, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/ditto \"$APP_PATH\" \"$COPIED_APP_PATH\"", validateDmgPackageScript, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-signature.sh"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "aria2-runtime-integrity.sh\" verify"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-loader.sh"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-bundle-launch.swift"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-aria2-rpc-readiness.sh"));
        Assert.Equal(2, CountOccurrences(validateDmgPackageScript, "validate_app_boundary \"$"));

        Assert.Contains("runtime checksum path must remain a symlink", ariaIntegrityScript, StringComparison.Ordinal);
        Assert.Contains("Contents/_CodeSignature/CodeResources", ariaIntegrityScript, StringComparison.Ordinal);
        Assert.Contains("refusing to modify the runtime checksum after the outer app signature is sealed", ariaIntegrityScript, StringComparison.Ordinal);
        Assert.DoesNotContain("lipo -archs", ariaIntegrityScript, StringComparison.Ordinal);
        Assert.Contains("aria2.getVersion", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("downkyi-secure-redirect-v2", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("isinstance(features, list)", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("--help=#all", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("\"$PROBE_ROOT/dht.dat\"", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("\"$PROBE_ROOT/dht6.dat\"", ariaReadinessScript, StringComparison.Ordinal);
        Assert.Contains("aria2.shutdown", ariaReadinessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("aria2-runtime-integrity.sh", ariaReadinessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--connect-timeout", ariaReadinessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--max-time", ariaReadinessScript, StringComparison.Ordinal);
        Assert.DoesNotContain("bounded deadline", ariaReadinessScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MacBuildKeepsCredentialFreePackageValidation()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var macSteps = GetWorkflowSteps(workflow, "  build-macos:");

        Assert.Contains("MACOS_ADHOC_SIGNING: ${{ env.HAS_MACOS_SIGNING != 'true' }}", workflow, StringComparison.Ordinal);
        foreach (var stepName in new[]
                 {
                     "Run macOS packaging regressions",
                     "Build ${{ matrix.cpu }}",
                     "Package app",
                     "Sign app",
                     "Verify signed app trust",
                     "Verify signed aria2 integrity",
                     "Create DMG",
                     "Validate mounted and installed macOS package contracts"
                 })
        {
            AssertStepHasNoCondition(macSteps, stepName);
        }

        foreach (var stepName in new[]
                 {
                     "Import certificate",
                     "Resolve signing identity",
                     "Notarize app",
                     "Verify notarized app trust",
                     "Sign DMG",
                     "Verify signed DMG",
                     "Notarize DMG",
                     "Verify notarized DMG"
                 })
        {
            AssertStepCondition(macSteps, stepName, "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        }
    }

    [Fact]
    public void MacAdHocPackageWorkflowCoversBothRidsWithoutAppleCredentials()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "macos-adhoc-package.yml"));
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var pullRequest = GetYamlBlock(lines, "  pull_request:", 2);
        var pathBlock = GetYamlBlock(pullRequest.ToArray(), "    paths:", 4);
        var triggerPaths = pathBlock
            .Where(line => GetIndent(line) == 6 && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.Trim()[2..].Trim('\'', '"'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("runs-on: ${{ matrix.os }}", workflow, StringComparison.Ordinal);
        Assert.Contains("os: macos-26", workflow, StringComparison.Ordinal);
        Assert.Contains("os: macos-15-intel", workflow, StringComparison.Ordinal);
        Assert.Contains("runtime: osx-x64", workflow, StringComparison.Ordinal);
        Assert.Contains("runtime: osx-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("MACOS_ADHOC_SIGNING: 'true'", workflow, StringComparison.Ordinal);
        foreach (var packageInput in new[]
                 {
                     "DownKyi/**/*.csproj",
                     "DownKyi.Core/**/*.csproj",
                     "src/**/*.csproj",
                     "src/DownKyi.Desktop/Resources/favicon.ico",
                     "THIRD-PARTY-NOTICES.md",
                     "script/aria2.sh",
                     "script/ffmpeg.sh",
                     "script/ffmpeg-assets.py",
                     "script/validate-publish-output.ps1",
                     "script/assets/**",
                     "script/macos/**"
                 })
        {
            Assert.Contains(packageInput, triggerPaths);
        }

        foreach (var broadOrDuplicateInput in new[]
                 {
                     "DownKyi/**",
                     "DownKyi.Core/**",
                     "src/**",
                     "script/test-project.ps1",
                     "script/test-project-runner.ps1",
                     "tests/DownKyi.MacOS.Tests/**",
                     "tests/PlatformShared/**",
                     "tools/DownKyi.CentralTestRunner/**"
                 })
        {
            Assert.DoesNotContain(broadOrDuplicateInput, triggerPaths);
        }

        Assert.Contains("dotnet publish", workflow, StringComparison.Ordinal);
        Assert.Contains("./sign.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("./verify-app-signature.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("./aria2-runtime-integrity.sh verify", workflow, StringComparison.Ordinal);
        Assert.Contains("flags=.*runtime", workflow, StringComparison.Ordinal);
        Assert.Contains("create-dmg", workflow, StringComparison.Ordinal);
        Assert.Contains("./validate-dmg-package.sh", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "- name: Upload verified DMG\n        continue-on-error: ${{ github.event_name == 'pull_request' }}",
            workflow.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Verify pre-sign aria2 supply-chain boundary", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Run macOS packaging regressions", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload macOS test evidence", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("script/test-project.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("notarytool", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("stapler", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("spctl", workflow, StringComparison.Ordinal);
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

    private static bool HasSafeReferenceAssemblyConsumerPolicy(XDocument props)
    {
        var settings = props
            .Descendants()
            .Where(element => element.Name.LocalName == "CompileUsingReferenceAssemblies")
            .ToArray();

        return settings.Length == 1 &&
               settings[0].Attribute("Condition") is null &&
               string.Equals(settings[0].Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
    }

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

    private static void AssertStepContinueOnError(
        IReadOnlyList<List<string>> steps,
        string stepName,
        string expectedValue)
    {
        var step = FindWorkflowStep(steps, stepName);
        Assert.Contains(
            step,
            line => GetIndent(line) == 8 &&
                    string.Equals(
                        line.Trim(),
                        $"continue-on-error: {expectedValue}",
                        StringComparison.Ordinal));
    }

    private static void AssertStepHasNoContinueOnError(
        IReadOnlyList<List<string>> steps,
        string stepName)
    {
        var step = FindWorkflowStep(steps, stepName);
        Assert.DoesNotContain(
            step,
            line => GetIndent(line) == 8 &&
                    line.Trim().StartsWith("continue-on-error:", StringComparison.Ordinal));
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
