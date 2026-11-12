using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

[Collection(PowerShellProcessTestGroup.Name)]
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
        foreach (var jobName in new[]
                 {
                     "release-gate",
                     "build-windows",
                     "build-linux-publish",
                     "build-linux",
                     "validate-linux-arm64",
                     "build-macos",
                     "pre-release-validation"
                 })
        {
            var job = GetYamlBlock(NormalizeWorkflowLines(workflow), $"  {jobName}:", 2);
            Assert.Contains("      fail-fast: false", job);
        }

        foreach (var jobName in new[] { "build-windows", "build-linux-publish", "build-macos" })
        {
            var job = GetYamlBlock(NormalizeWorkflowLines(workflow), $"  {jobName}:", 2);
            Assert.Contains(job, line => line.Contains("validate-publish-output.ps1", StringComparison.Ordinal));
        }

        foreach (var jobName in new[] { "build-windows", "build-linux", "validate-linux-arm64", "build-macos" })
        {
            var job = GetYamlBlock(NormalizeWorkflowLines(workflow), $"  {jobName}:", 2);
            Assert.Contains(job, line => line.Contains("write-package-checksum.ps1", StringComparison.Ordinal));
        }

        Assert.Contains("./script/assemble-release-assets.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/resolve-release-plan.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts: artifacts/release-assets/*", workflow, StringComparison.Ordinal);
        Assert.Contains("immutableCreate: true", workflow, StringComparison.Ordinal);
        Assert.Contains("artifactErrorsFailBuild: true", workflow, StringComparison.Ordinal);
        Assert.Contains("allowUpdates: false", workflow, StringComparison.Ordinal);
        Assert.Contains("replacesArtifacts: false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("IMMUTABLE_RELEASES_READ_TOKEN", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("repos/$GITHUB_REPOSITORY/immutable-releases", workflow, StringComparison.Ordinal);
        Assert.Contains("Verify published release identity", workflow, StringComparison.Ordinal);
        Assert.Contains("./script/verify-published-release.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("artifacts: artifacts/*", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowsShareOnePackageChecksumSidecarOwner()
    {
        var releaseWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var adHocWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "macos-adhoc-package.yml"));
        var checksumWriter = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "write-package-checksum.ps1"));

        Assert.Equal(4, Regex.Count(releaseWorkflow, "write-package-checksum\\.ps1"));
        Assert.Equal(1, Regex.Count(adHocWorkflow, "write-package-checksum\\.ps1"));
        Assert.DoesNotContain("Get-FileHash", releaseWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-FileHash", adHocWorkflow, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", checksumWriter, StringComparison.Ordinal);
        Assert.Contains(".ToLowerInvariant()", checksumWriter, StringComparison.Ordinal);
        Assert.Contains("$hash  $($package.Name)", checksumWriter, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackageIdentityHasOneScriptOwner()
    {
        var packages = ReleasePackageCatalogTestSupport.LoadPackages("9.8.7");
        var validationMatrix = ReleasePackageCatalogTestSupport.LoadValidationMatrix("9.8.7");
        var validator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-build-run-artifacts.ps1"));
        var assembler = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "assemble-release-assets.ps1"));
        var resolver = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "resolve-release-plan.ps1"));

        Assert.Equal(9, packages.Length);
        Assert.Equal(packages.Length, packages.Select(package => package.PackageName).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(packages.Length, packages.Select(package => package.ManifestName).Distinct(StringComparer.Ordinal).Count());

        var expectedScopes = packages.Select(package => package.Scope)
            .Append("Inventory")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedScopes,
            validationMatrix.Include.Select(target => target.Scope).Order(StringComparer.Ordinal).ToArray());

        var matrixArtifacts = validationMatrix.Include
            .SelectMany(target => new[] { target.Pattern, target.Pattern2, target.Pattern3 })
            .Where(pattern => pattern.Length > 0 && pattern != "*")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var artifactName = package.Transport is null
                ? package.PackageName
                : package.Transport.Replace(".transport.tar", "-transport", StringComparison.Ordinal);
            Assert.Contains(artifactName, matrixArtifacts);
        }

        Assert.Contains("Get-ReleasePackageCatalog", validator, StringComparison.Ordinal);
        Assert.Contains("Get-ReleasePackageCatalog", assembler, StringComparison.Ordinal);
        Assert.Contains("Get-ReleaseValidationMatrix", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-manifest-win-x64.json", validator, StringComparison.Ordinal);
        Assert.DoesNotContain("publish-manifest-win-x64.json", assembler, StringComparison.Ordinal);
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
        var releasePlan = GetYamlBlock(
            workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            "  release-plan:",
            2);

        Assert.Contains(
            "    needs: [changelog, release-plan, build-windows, build-linux, validate-linux-arm64, build-macos]",
            preRelease);
        Assert.Contains("      fail-fast: false", preRelease);
        Assert.Contains("      matrix: ${{ fromJSON(needs.release-plan.outputs.validation_matrix) }}", preRelease);
        Assert.Contains(releasePlan, line => line.Contains("./script/resolve-release-plan.ps1", StringComparison.Ordinal));
        Assert.Contains(releasePlan, line => line.Contains("validation_matrix=$matrix", StringComparison.Ordinal));
        Assert.Contains("      - name: Download all artifacts from this Build run", preRelease);
        Assert.Contains("          merge-multiple: true", preRelease);
        Assert.Contains(
            preRelease,
            line => line.Contains("./script/validate-build-run-artifacts.ps1", StringComparison.Ordinal));
        Assert.DoesNotContain(preRelease, line => line.Contains("dotnet build", StringComparison.Ordinal));
        Assert.DoesNotContain(preRelease, line => line.Contains("dotnet publish", StringComparison.Ordinal));

        var preReleaseSteps = GetWorkflowSteps(workflow, "  pre-release-validation:");
        AssertStepCondition(
            preReleaseSteps,
            "Upload downloaded package validation diagnostics",
            "${{ failure() && (matrix.scope == 'Windows' || startsWith(matrix.scope, 'Linux')) }}");
        var diagnosticsUpload = FindWorkflowStep(
            preReleaseSteps,
            "Upload downloaded package validation diagnostics");
        Assert.Contains("        uses: $/.github/actions/upload-artifact", diagnosticsUpload);
        Assert.Contains(
            "          name: pre-release-${{ matrix.scope }}-validation-diagnostics",
            diagnosticsUpload);
        Assert.Contains(
            "          path: artifacts/pre-release-validation/*.failure-*",
            diagnosticsUpload);
        Assert.Contains("          if-no-files-found: ignore", diagnosticsUpload);

        Assert.Contains("    needs: [changelog, pre-release-validation]", release);
        Assert.Contains(
            release,
            line => line.Contains("./script/validate-build-run-artifacts.ps1", StringComparison.Ordinal));
        Assert.Contains(release, line => line.Contains("-Scope Inventory", StringComparison.Ordinal));
        Assert.Contains(
            release,
            line => line.Contains("./script/assemble-release-assets.ps1", StringComparison.Ordinal));
        AssertInOrder(
            string.Join('\n', release),
            "./script/validate-build-run-artifacts.ps1",
            "./script/assemble-release-assets.ps1",
            "artifacts: artifacts/release-assets/*",
            "./script/verify-published-release.ps1");
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
    public void BuildHasOneReleasePathAndTheUpdaterHasItsOwnEntryPoint()
    {
        var build = ReadWorkflow("build.yml");
        var updater = ReadWorkflow("update-ffmpeg-assets.yml");
        var pullRequest = GetYamlBlock(NormalizeWorkflowLines(build), "  pull_request:", 2);
        var paths = GetYamlSequenceValues(pullRequest, "    paths:", 4);

        Assert.Equal("script/assets/external-assets.json", Assert.Single(paths));
        Assert.DoesNotContain("ffmpeg-asset-updater:", build, StringComparison.Ordinal);
        Assert.DoesNotContain("detect-production-manifest-change:", build, StringComparison.Ordinal);
        Assert.DoesNotContain("ffmpeg-required-gate:", build, StringComparison.Ordinal);
        Assert.Contains("  schedule:", updater, StringComparison.Ordinal);
        Assert.Contains("  workflow_dispatch:", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("  workflow_call:", updater, StringComparison.Ordinal);
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
            (".github/actions/build-macos-app/action.yml", ["macos"]),
            ("script/macos/validate-dmg-package.sh", ["macos"]),
            ("script/download-external-asset.ps1", []),
            ("script/resolve-release-plan.ps1", []),
            ("script/ffmpeg-assets.py", ["macos"]),
            ("script/assets/external-assets.json", ["build", "macos"]),
            (".github/workflows/build.yml", []),
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
    public void BuildDependenciesRequireThePreflightAndDownloadedArtifactGate()
    {
        var lines = NormalizeWorkflowLines(ReadWorkflow("build.yml"));
        var preflight = GetYamlBlock(lines, "  external-assets-preflight:", 2);
        var releaseGate = GetYamlBlock(lines, "  release-gate:", 2);
        var preRelease = GetYamlBlock(lines, "  pre-release-validation:", 2);
        var release = GetYamlBlock(lines, "  release:", 2);

        Assert.DoesNotContain(preflight, line => GetIndent(line) == 4 &&
            (line.TrimStart().StartsWith("if:", StringComparison.Ordinal) ||
             line.TrimStart().StartsWith("needs:", StringComparison.Ordinal)));
        Assert.Contains("    needs: external-assets-preflight", releaseGate);
        Assert.DoesNotContain(releaseGate, line =>
            GetIndent(line) == 4 && line.TrimStart().StartsWith("if:", StringComparison.Ordinal));
        Assert.Contains(
            "    needs: [changelog, release-plan, build-windows, build-linux, validate-linux-arm64, build-macos]",
            preRelease);
        AssertJobConditionContains(preRelease, "github.event_name != 'pull_request'");
        Assert.Contains("    needs: [changelog, pre-release-validation]", release);
        AssertJobConditionContains(release, "startsWith(github.ref, 'refs/tags/')");
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
    public void FfmpegToolingChecksRunInTheRequiredPrJob()
    {
        var qualitySteps = GetWorkflowSteps(ReadWorkflow("quality.yml"), "  format:");
        var preflight = GetYamlBlock(NormalizeWorkflowLines(ReadWorkflow("build.yml")),
            "  external-assets-preflight:", 2);

        Assert.Contains(FindWorkflowStep(qualitySteps, "Run FFmpeg asset guards"),
            line => line.Contains("python -m unittest script/tests/test_ffmpeg_assets.py -v",
                StringComparison.Ordinal));
        Assert.Contains(FindWorkflowStep(qualitySteps, "Validate GitHub Actions workflows"),
            line => line.Contains("uses: $/.github/actions/actionlint", StringComparison.Ordinal));
        AssertStepHasNoContinueOnError(qualitySteps, "Run FFmpeg asset guards");
        AssertStepHasNoContinueOnError(qualitySteps, "Validate GitHub Actions workflows");
        Assert.Contains(preflight, line => line.Contains(
            "python script/ffmpeg-assets.py validate-manifest --manifest script/assets/external-assets.json",
            StringComparison.Ordinal));
        Assert.Contains(preflight, line => line.Contains(
            "python script/ffmpeg-assets.py preflight --manifest script/assets/external-assets.json --timeout 30",
            StringComparison.Ordinal));
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
    public void PublishValidatorRequiresBothMediaTools()
    {
        var validator = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "validate-publish-output.ps1"));

        Assert.Contains("ffmpeg/ffmpeg", validator, StringComparison.Ordinal);
        Assert.Contains("ffmpeg/ffprobe", validator, StringComparison.Ordinal);
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

        foreach (var tool in manifest.RootElement.EnumerateObject())
        {
            foreach (var asset in tool.Value.GetProperty("assets").EnumerateObject())
            {
                AssertPinnedAsset(asset.Value, "url", "sha256");

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
            File.ReadAllText(Path.Combine(RepositoryRoot, "script", "ffmpeg.sh")),
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
        Assert.Contains("BASH_SOURCE[0]", scripts[1], StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsExternalAssetInstallerUsesTheSharedBoundedRetryOwner()
    {
        var ffmpegInstaller = File.ReadAllText(Path.Combine(RepositoryRoot, "script", "ffmpeg.ps1"));

        Assert.Contains("download-external-asset.ps1", ffmpegInstaller, StringComparison.Ordinal);
        Assert.Contains("Invoke-ExternalAssetDownload", ffmpegInstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-BitsTransfer", ffmpegInstaller, StringComparison.Ordinal);
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
    public void MacWorkflowsShareOneAppBuildAction()
    {
        var action = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "actions", "build-macos-app", "action.yml"));
        var releaseWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var adHocWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "macos-adhoc-package.yml"));

        foreach (var workflow in new[] { releaseWorkflow, adHocWorkflow })
        {
            Assert.Contains("uses: ./.github/actions/build-macos-app", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("dotnet publish DownKyi/DownKyi.csproj", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("./ffmpeg.sh mac ${{ matrix.cpu }}", workflow, StringComparison.Ordinal);
        }

        Assert.Contains(
            "dotnet restore DownKyi/DownKyi.csproj -r osx-${{ inputs.cpu }}",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet publish DownKyi/DownKyi.csproj --no-restore --self-contained -r osx-${{ inputs.cpu }}",
            action,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MacWorkflowsShareOneDmgLayoutOwner()
    {
        var releaseWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var adHocWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "macos-adhoc-package.yml"));
        var createDmgScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "script", "macos", "create-dmg.sh"));

        foreach (var workflow in new[] { releaseWorkflow, adHocWorkflow })
        {
            Assert.Contains("./create-dmg.sh ${{ steps.macos-app.outputs.version }} ${{ matrix.cpu }}", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("--icon-size", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("--window-size", workflow, StringComparison.Ordinal);
        }

        Assert.Contains("create-dmg", createDmgScript, StringComparison.Ordinal);
        Assert.Contains("--icon-size 120", createDmgScript, StringComparison.Ordinal);
        Assert.Contains("--app-drop-link 495 175", createDmgScript, StringComparison.Ordinal);
        Assert.Contains("--window-size 660 400", createDmgScript, StringComparison.Ordinal);
        Assert.Contains("x64|arm64", createDmgScript, StringComparison.Ordinal);
    }

    [Fact]
    public void MacReleaseAdHocSignsAndVerifiesFinalArtifacts()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "build.yml"));
        var appBuildAction = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "actions", "build-macos-app", "action.yml"));
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
        AssertInOrder(
            workflow,
            "Build and validate macOS app bundle",
            "Sign app",
            "Verify signed app trust",
            "Notarize app",
            "Verify notarized app trust",
            "Seal final signed app manifest",
            "Create DMG",
            "Sign DMG",
            "Verify signed DMG",
            "Notarize DMG",
            "Verify notarized DMG",
            "Validate mounted and installed macOS package contracts",
            "Hash DMG",
            "Upload build artifacts");
        AssertInOrder(
            appBuildAction,
            "Read version",
            "Download and verify upstream runtime dependencies",
            "Publish app",
            "Package app bundle",
            "Validate packaged runtime");

        Assert.DoesNotContain("biao yao", signScript, StringComparison.Ordinal);
        Assert.DoesNotContain("codesign --deep --force", signScript, StringComparison.Ordinal);
        Assert.Contains("resolve_signing_identity", signScript, StringComparison.Ordinal);
        Assert.Contains("find \"$APP_NAME/Contents\" -type f", signScript, StringComparison.Ordinal);
        Assert.Contains("is_signable_app_file \"$file\"", signScript, StringComparison.Ordinal);
        Assert.Contains("codesign_app_path \"$file\"", signScript, StringComparison.Ordinal);
        AssertInOrder(
            signScript,
            "codesign_app_path \"$file\"",
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
        Assert.Contains(
            "manifest-path: artifacts/macos/pre-sign-publish-manifest-osx-${{ matrix.cpu }}.json",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("-OutputPath \"${{ inputs.manifest-path }}\"", appBuildAction, StringComparison.Ordinal);
        Assert.Contains(
            "-PublishDirectory \"script/macos/哔哩下载姬.app/Contents/MacOS\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-ExpectedManifestPath \"$EXPECTED_MANIFEST_PATH\"",
            validateDmgPackageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "/bin/bash \"$SCRIPT_DIR/verify-runtime-architecture.sh\"",
            validateDmgPackageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "/bin/bash \"$SCRIPT_DIR/verify-app-signature.sh\"",
            validateDmgPackageScript,
            StringComparison.Ordinal);
        Assert.Contains("/usr/bin/ditto \"$APP_PATH\" \"$COPIED_APP_PATH\"", validateDmgPackageScript, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-signature.sh"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-loader.sh"));
        Assert.Equal(1, CountOccurrences(validateDmgPackageScript, "verify-app-bundle-launch.swift"));
        Assert.Equal(2, CountOccurrences(validateDmgPackageScript, "validate_app_boundary \"$"));
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
                     "Build and validate macOS app bundle",
                     "Sign app",
                     "Verify signed app trust",
                     "Seal final signed app manifest",
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
        var appBuildAction = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "actions", "build-macos-app", "action.yml"));
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
                     "script/ffmpeg.sh",
                     "script/ffmpeg-assets.py",
                     "script/validate-publish-output.ps1",
                     "script/assets/**",
                     "script/macos/**",
                     ".github/actions/build-macos-app/action.yml"
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

        Assert.Contains("uses: ./.github/actions/build-macos-app", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet publish", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", appBuildAction, StringComparison.Ordinal);
        Assert.Contains(
            "manifest-path: script/macos/publish-manifest-osx-${{ matrix.cpu }}.json",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("./sign.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("./verify-app-signature.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("flags=.*runtime", workflow, StringComparison.Ordinal);
        Assert.Contains("./create-dmg.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("./validate-dmg-package.sh", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "- name: Upload verified DMG\n        continue-on-error: ${{ github.event_name == 'pull_request' }}",
            workflow.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Run macOS packaging regressions", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload macOS test evidence", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("script/test-project.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("notarytool", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("stapler", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("spctl", workflow, StringComparison.Ordinal);
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

    private static void AssertJobConditionContains(
        IReadOnlyList<string> job,
        params string[] requiredFragments)
    {
        var condition = Assert.Single(job, line =>
            GetIndent(line) == 4 && line.TrimStart().StartsWith("if:", StringComparison.Ordinal));
        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, condition, StringComparison.Ordinal);
        }
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
                !line.TrimStart().StartsWith('#', StringComparison.Ordinal) &&
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
