using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class ReleaseWorkflowArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ReleaseWorkflowKeepsStrictCrossPlatformGateAndDownloadedPackageValidation()
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
        Assert.Equal(7, CountOccurrences(workflow, "fail-fast: false"));
        Assert.Equal(4, CountOccurrences(workflow, "validate-publish-output.ps1"));
        Assert.Equal(4, CountOccurrences(workflow, "Get-FileHash"));
    }

    [Fact]
    public void PreReleaseGateDownloadsTheSameRunAndBlocksPublication()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var preRelease = GetYamlBlock(
            workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            "  pre-release-validation:",
            2);
        var release = GetYamlBlock(
            workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            "  release:",
            2);

        Assert.Contains(
            "    needs: [changelog, build-windows, build-linux, validate-linux-arm64, build-macos]",
            preRelease);
        Assert.Contains("      fail-fast: false", preRelease);
        Assert.Contains("          - scope: Inventory", preRelease);
        Assert.Contains("          - scope: Windows", preRelease);
        Assert.Contains("          - scope: LinuxX64", preRelease);
        Assert.Contains("          - scope: LinuxArm64", preRelease);
        Assert.Contains("          - scope: MacOSX64", preRelease);
        Assert.Contains("          - scope: MacOSArm64", preRelease);
        Assert.Contains("      - name: Download all artifacts from this Build run", preRelease);
        Assert.Contains("          merge-multiple: true", preRelease);
        Assert.Contains(
            preRelease,
            line => line.Contains("./script/validate-build-run-artifacts.ps1", StringComparison.Ordinal));
        Assert.DoesNotContain(preRelease, line => line.Contains("dotnet build", StringComparison.Ordinal));
        Assert.DoesNotContain(preRelease, line => line.Contains("dotnet publish", StringComparison.Ordinal));

        Assert.Contains("    needs: [changelog, pre-release-validation]", release);
        Assert.Contains(
            release,
            line => line.Contains("./script/validate-build-run-artifacts.ps1", StringComparison.Ordinal));
        Assert.Contains(release, line => line.Contains("-Scope Inventory", StringComparison.Ordinal));
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
                "script/install-appimagetool.ps1",
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
    public void SelectiveCiTriggersMatchRepresentativeChangesAndEvents()
    {
        var workflows = new Dictionary<string, string>
        {
            ["quality"] = ReadWorkflow("quality.yml"),
            ["codeql"] = ReadWorkflow("codeql.yml"),
            ["dependency"] = ReadWorkflow("dependency-audit.yml"),
            ["build"] = ReadWorkflow("build.yml"),
            ["macos"] = ReadWorkflow("macos-adhoc-package.yml"),
            ["aria2"] = ReadWorkflow("aria2-tls-security.yml"),
            ["infrastructure"] = ReadWorkflow("ci-infrastructure.yml")
        };

        // Each case is evaluated against the real event and glob filters, not a copied path list.
        var pullRequests = new (string Path, string[] ExtraWorkflows)[]
        {
            ("docs/maintenance.md", []),
            ("src/DownKyi.Application/Example.cs", []),
            ("tests/DownKyi.Architecture.Tests/ReleaseSafetyRegressionTests.cs", []),
            ("docs/testing/test-runner-policy.json", ["infrastructure"]),
            ("tests/DownKyi.Windows.Tests/WindowsEtwResourceFlightRecorderTests.cs", ["infrastructure"]),
            ("script/validate-build-run-artifacts.ps1", []),
            ("script/pupnet/stage-canonical-publish.sh", []),
            ("script/validate-publish-output.ps1", ["macos"]),
            ("script/macos/validate-dmg-package.sh", ["macos"]),
            ("script/aria2.sh", ["macos", "aria2"]),
            ("script/download-external-asset.ps1", ["build", "aria2"]),
            ("script/ffmpeg-assets.py", ["build", "macos"]),
            ("script/assets/external-assets.json", ["build", "macos", "aria2"]),
            (".github/workflows/build.yml", ["build"]),
            (".github/workflows/quality.yml", ["infrastructure"])
        };
        foreach (var (path, extraWorkflows) in pullRequests)
        {
            foreach (var (name, workflow) in workflows)
            {
                var expected = name is "quality" or "codeql" or "dependency" ||
                               extraWorkflows.Contains(name, StringComparer.Ordinal);
                Assert.Equal(expected, WorkflowRuns(workflow, "pull_request", path));
            }
        }

        Assert.True(WorkflowRuns(workflows["quality"], "push-main", "docs/maintenance.md"));
        Assert.True(WorkflowRuns(workflows["codeql"], "push-main", "docs/maintenance.md"));
        Assert.False(WorkflowRuns(workflows["dependency"], "push-main", "docs/maintenance.md"));
        Assert.False(WorkflowRuns(workflows["build"], "push-main", "script/assets/external-assets.json"));
        Assert.True(WorkflowRuns(workflows["aria2"], "push-main", "script/assets/external-assets.json"));
        Assert.True(WorkflowRuns(workflows["build"], "push-tag", "docs/maintenance.md"));
        Assert.False(WorkflowRuns(workflows["quality"], "push-tag", "docs/maintenance.md"));
        Assert.True(WorkflowRuns(workflows["build"], "workflow_dispatch", ""));
        Assert.True(WorkflowRuns(workflows["macos"], "workflow_dispatch", ""));
        Assert.False(WorkflowRuns(workflows["quality"], "workflow_dispatch", ""));

        // A combined PR takes the union of path matches.
        Assert.True(WorkflowRuns(workflows["build"], "pull_request",
            "script/validate-build-run-artifacts.ps1", "script/assets/external-assets.json"));
    }

    [Fact]
    public void BuildJobDependenciesPreserveExpectedSkipsAndFormalReleaseGate()
    {
        var workflow = ReadWorkflow("build.yml");

        Assert.False(BuildJobRuns(workflow, "release-gate", "pull_request",
            "script/validate-build-run-artifacts.ps1"));
        Assert.True(BuildJobRuns(workflow, "ffmpeg-required-gate", "pull_request",
            ".github/workflows/build.yml"));
        Assert.False(BuildJobRuns(workflow, "external-assets-preflight", "pull_request",
            ".github/workflows/build.yml"));
        Assert.False(BuildJobRuns(workflow, "release-gate", "pull_request",
            ".github/workflows/build.yml"));
        Assert.True(BuildJobRuns(workflow, "release-gate", "pull_request",
            "script/assets/external-assets.json"));
        Assert.False(BuildJobRuns(workflow, "pre-release-validation", "pull_request",
            "script/assets/external-assets.json"));
        Assert.False(BuildJobRuns(workflow, "release", "pull_request",
            "script/assets/external-assets.json"));
        Assert.True(BuildJobRuns(workflow, "pre-release-validation", "workflow_dispatch", ""));
        Assert.False(BuildJobRuns(workflow, "release", "workflow_dispatch", ""));
        Assert.True(BuildJobRuns(workflow, "ffmpeg-asset-updater", "workflow_dispatch", "",
            updateAssets: true));
        Assert.False(BuildJobRuns(workflow, "release-gate", "workflow_dispatch", "",
            updateAssets: true));
        Assert.True(BuildJobRuns(workflow, "pre-release-validation", "push-tag", ""));
        Assert.True(BuildJobRuns(workflow, "release", "push-tag", ""));
        Assert.False(BuildJobRuns(workflow, "ffmpeg-required-gate", "push-tag", ""));
    }

    [Fact]
    public void RequiredPullRequestChecksDoNotDependOnPathFilteredWorkflows()
    {
        var requiredJobs = new (string Workflow, string Job, string CheckName)[]
        {
            ("quality.yml", "format", "Format check"),
            ("quality.yml", "build-test", "Build and test (${{ matrix.check_name }})"),
            ("codeql.yml", "analyze", "Analyze C#"),
            ("dependency-audit.yml", "audit", "Dependency policy")
        };
        foreach (var (file, jobName, checkName) in requiredJobs)
        {
            var workflow = ReadWorkflow(file);
            Assert.True(WorkflowRuns(workflow, "pull_request", "docs/maintenance.md"));
            var job = GetYamlBlock(NormalizeWorkflowLines(workflow), $"  {jobName}:", 2);
            Assert.Contains($"    name: {checkName}", job);
            Assert.DoesNotContain(job, line => GetIndent(line) == 4 &&
                (line.TrimStart().StartsWith("if:", StringComparison.Ordinal) ||
                 line.TrimStart().StartsWith("needs:", StringComparison.Ordinal)));
        }
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
        Assert.DoesNotContain("ffmpeg_related:", detector);
        Assert.DoesNotContain("    needs: detect-production-manifest-change", tooling);
        Assert.Contains("      - name: Run FFmpeg asset guards", tooling);
        Assert.Contains("        run: python -m unittest script/tests/test_ffmpeg_assets.py -v", tooling);
        Assert.DoesNotContain("continue-on-error: true", tooling);
        Assert.Contains("        run: python script/ffmpeg-assets.py validate-manifest --manifest script/assets/external-assets.json", preflight);
        Assert.Contains("        run: python script/ffmpeg-assets.py preflight --manifest script/assets/external-assets.json --timeout 30", preflight);
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
        Assert.Contains("ExpectedManifestPath", validator, StringComparison.Ordinal);
        Assert.Contains("Published output contains user data paths", validator, StringComparison.Ordinal);
        Assert.Contains("'Aria', 'Logs', 'Storage', 'Config', 'Bilibili', 'Cache', 'Media'", validator, StringComparison.Ordinal);
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
        var criticalPathsStart = workflow.IndexOf("critical_paths=(", StringComparison.Ordinal);
        Assert.InRange(criticalPathsStart, 0, workflow.Length - 1);
        var criticalPathsEnd = workflow.IndexOf(')', criticalPathsStart);
        Assert.InRange(criticalPathsEnd, criticalPathsStart + 1, workflow.Length - 1);
        var criticalPaths = workflow[criticalPathsStart..criticalPathsEnd];
        Assert.Contains("script/test-project.ps1", criticalPaths, StringComparison.Ordinal);
        Assert.Contains("script/test-project-runner.ps1", criticalPaths, StringComparison.Ordinal);
        Assert.Contains("docs/testing/test-runner-policy.json", criticalPaths, StringComparison.Ordinal);
        Assert.Contains("tools/DownKyi.CentralTestRunner", criticalPaths, StringComparison.Ordinal);
        Assert.Contains("tools/DownKyi.ProcessSupervision", criticalPaths, StringComparison.Ordinal);
        Assert.Contains("Resolve macOS release trust mode", workflow, StringComparison.Ordinal);
        Assert.Contains("macos_trust_mode: ${{ steps.macos_trust.outputs.macos_trust_mode }}", workflow, StringComparison.Ordinal);
        Assert.Contains("HAS_MACOS_SIGNING: ${{ needs.authority.outputs.has_macos_signing }}", workflow, StringComparison.Ordinal);
        Assert.Contains("MACOS_ADHOC_SIGNING: ${{ env.HAS_MACOS_SIGNING != 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("if: ${{ env.HAS_MACOS_SIGNING == 'true' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify signed app trust", workflow, StringComparison.Ordinal);
        Assert.Contains("./validate-dmg-package.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("Render release notes for selected trust mode", workflow, StringComparison.Ordinal);
        Assert.Contains("bodyFile: tooling/artifacts/v1.1.2-release-notes.md", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Require Apple credentials for formal publish", workflow, StringComparison.Ordinal);

        var macSteps = GetWorkflowSteps(workflow, "  build-macos:");
        AssertStepCondition(macSteps, "Import Apple certificate", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Resolve Developer ID identity", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Notarize and verify app", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepCondition(macSteps, "Sign, notarize, and verify DMG", "${{ env.HAS_MACOS_SIGNING == 'true' }}");
        AssertStepHasNoCondition(macSteps, "Verify signed app trust");
        AssertStepHasNoCondition(macSteps, "Verify signed aria2 integrity");
        AssertStepHasNoCondition(macSteps, "Validate mounted and installed macOS package contracts");
        var signAppStep = FindWorkflowStep(macSteps, "Sign app");
        Assert.Contains(
            signAppStep,
            line => line.TrimStart().StartsWith("chmod +x ", StringComparison.Ordinal) &&
                    line.Contains("verify-runtime-architecture.sh", StringComparison.Ordinal));
        var packageStep = FindWorkflowStep(macSteps, "Package app with recovery tooling");
        Assert.Contains(
            packageStep,
            line => line.Trim() == "release_version=\"${EXPECTED_RELEASE_VERSION#v}\"");
        Assert.Contains(
            packageStep,
            line => line.Trim() == "./package.sh ${{ matrix.cpu }} \"$release_version\"");
        var verifyDmgStep = FindWorkflowStep(
            macSteps,
            "Validate mounted and installed macOS package contracts");
        Assert.Contains(
            verifyDmgStep,
            line => line.Trim() == "DownKyi-1.1.2-osx-${{ matrix.cpu }}.dmg \\");
        Assert.Contains(
            verifyDmgStep,
            line => line.Trim() == "\"$release_version\" \\");
        Assert.Contains(
            verifyDmgStep,
            line => line.Trim() == "osx-${{ matrix.cpu }}");
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
        process.WaitForExit();
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

    private static string ReadWorkflow(string name) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", name));

    private static string[] NormalizeWorkflowLines(string workflow) =>
        workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static bool WorkflowRuns(string workflow, string eventKind, params string[] changedPaths)
    {
        var on = GetYamlBlock(NormalizeWorkflowLines(workflow), "on:", 0);
        var eventName = eventKind switch
        {
            "pull_request" => "pull_request",
            "push-main" or "push-tag" => "push",
            "workflow_dispatch" => "workflow_dispatch",
            _ => throw new ArgumentOutOfRangeException(nameof(eventKind))
        };
        if (!on.Contains($"  {eventName}:", StringComparer.Ordinal))
        {
            return false;
        }

        var eventBlock = GetYamlBlock(on.ToArray(), $"  {eventName}:", 2);
        if (eventKind is "pull_request" or "push-main" or "push-tag")
        {
            Assert.DoesNotContain(eventBlock, line =>
                GetIndent(line) == 4 && line.Trim() == "paths-ignore:");
            var refKey = eventKind == "push-tag" ? "tags" : "branches";
            var refFilters = GetYamlSequenceValues(eventBlock, $"    {refKey}:", 4);
            if (eventKind == "push-tag" && !refFilters.Any(filter =>
                    GlobMatches(filter, "v1.2.2")))
            {
                return false;
            }
            if (eventKind == "push-main" && !refFilters.Any(filter =>
                    GlobMatches(filter, "main")))
            {
                return false;
            }
            if (eventKind == "pull_request" && refFilters.Length > 0 &&
                !refFilters.Any(filter => GlobMatches(filter, "main")))
            {
                return false;
            }

            var pathFilters = GetYamlSequenceValues(eventBlock, "    paths:", 4);
            if (pathFilters.Length > 0 && !changedPaths.Any(path =>
                    pathFilters.Any(filter => GlobMatches(filter, path))))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] GetYamlSequenceValues(
        IReadOnlyList<string> parent, string header, int indent) =>
        GetYamlBlock(parent.ToArray(), header, indent)
            .Where(line => GetIndent(line) == indent + 2 &&
                           line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.Trim()[2..].Trim('\'', '"'))
            .ToArray();

    private static bool GlobMatches(string glob, string path)
    {
        var pattern = Regex.Escape(glob)
            .Replace(@"\*\*/", @"(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return Regex.IsMatch(path, $"\\A{pattern}\\z", RegexOptions.CultureInvariant);
    }

    private static bool BuildJobRuns(
        string workflow, string jobName, string eventKind, string changedPath,
        bool updateAssets = false)
    {
        if (!WorkflowRuns(workflow, eventKind, changedPath))
        {
            return false;
        }

        return BuildJobRunsCore(workflow, jobName, eventKind, changedPath, updateAssets);
    }

    private static bool BuildJobRunsCore(
        string workflow, string jobName, string eventKind, string changedPath,
        bool updateAssets)
    {
        var job = GetYamlBlock(NormalizeWorkflowLines(workflow), $"  {jobName}:", 2);
        Assert.NotEmpty(job);
        var needsLine = job.SingleOrDefault(line => GetIndent(line) == 4 &&
            line.TrimStart().StartsWith("needs:", StringComparison.Ordinal));
        var needs = needsLine is null ? [] : needsLine.Trim() == "needs:"
            ? GetYamlSequenceValues(job, "    needs:", 4)
            : needsLine.Trim()["needs:".Length..].Trim().Trim('[', ']')
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var results = needs.ToDictionary(
            need => need,
            need => BuildJobRunsCore(workflow, need, eventKind, changedPath, updateAssets),
            StringComparer.Ordinal);
        var condition = job.SingleOrDefault(line => GetIndent(line) == 4 &&
            line.TrimStart().StartsWith("if:", StringComparison.Ordinal))?.Trim();
        var dependenciesSucceeded = results.Values.All(result => result);

        return condition switch
        {
            null => dependenciesSucceeded,
            "if: ${{ inputs.update_ffmpeg_assets }}" =>
                dependenciesSucceeded && updateAssets,
            "if: ${{ always() && !inputs.update_ffmpeg_assets && needs.ffmpeg-tooling.result == 'success' && (github.event_name != 'pull_request' || needs.detect-production-manifest-change.outputs.external_assets == 'true') }}" =>
                !updateAssets && results["ffmpeg-tooling"] &&
                (eventKind != "pull_request" ||
                 changedPath == "script/assets/external-assets.json"),
            "if: ${{ always() && github.event_name == 'pull_request' }}" =>
                eventKind == "pull_request",
            "if: ${{ always() && needs.release-gate.result == 'success' }}" =>
                results["release-gate"],
            "if: ${{ github.event_name != 'pull_request' && !inputs.update_ffmpeg_assets }}" =>
                dependenciesSucceeded && eventKind != "pull_request" && !updateAssets,
            "if: ${{ startsWith(github.ref, 'refs/tags/') }}" =>
                dependenciesSucceeded && eventKind == "push-tag",
            _ => throw new InvalidOperationException($"Unmodeled Build job condition in {jobName}: {condition}")
        };
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
