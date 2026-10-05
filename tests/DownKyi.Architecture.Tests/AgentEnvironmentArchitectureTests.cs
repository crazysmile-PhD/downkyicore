using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DownKyi.Architecture.Tests;

public sealed class AgentEnvironmentArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string WorkManagementProjectUrl = "https://github.com/users/crazysmile-PhD/projects/2";

    [Fact]
    public void RepositoryStructureSeparatesSourceTestsDocumentationConfigurationAndScripts()
    {
        AssertPathsExist(
            "DownKyi",
            "DownKyi.Core",
            "src",
            "tests",
            "docs",
            "script",
            "Directory.Build.props",
            "Directory.Packages.props");
    }

    [Fact]
    public void RealSystemStateHasObservableEntrypoints()
    {
        AssertPathsExist(
            "script/audit-module-boundaries.ps1",
            "src/DownKyi.Infrastructure/Logging/ApplicationLogProvider.cs",
            "tests/DownKyi.Desktop.Tests/UiSmokeTests.cs",
            "benchmarks/DownKyi.SystemBenchmarks",
            "docs/operations/verification-and-rollback.md",
            "docs/performance-baseline.md");
    }

    [Fact]
    public void BuildRunAndTestInputsAreReproducible()
    {
        AssertPathsExist(
            "global.json",
            ".python-version",
            "DownKyi.sln",
            "version.txt",
            "Directory.Packages.props",
            "script/test-solution.ps1",
            "script/test-project.ps1",
            "script/test-project-runner.ps1",
            "script/classify-resource-contention.ps1",
            "tools/DownKyi.CentralTestRunner/DownKyi.CentralTestRunner.csproj",
            "docs/maintenance.md",
            "docs/testing/targeted-resource-forensics.md",
            "docs/operations/verification-and-rollback.md");

        var operations = Read("docs/operations/verification-and-rollback.md");
        Assert.Contains("dotnet restore ./DownKyi.sln", operations, StringComparison.Ordinal);
        Assert.Contains("dotnet build ./DownKyi.sln", operations, StringComparison.Ordinal);
        Assert.Contains("script/test-solution.ps1", operations, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationReadsToolchainVersionsFromRepositoryOwners()
    {
        Assert.Matches(
            "\\\"version\\\"\\s*:\\s*\\\"\\d+\\.\\d+\\.\\d+\\\"",
            Read("global.json"));
        Assert.Matches(
            @"^\d+\.\d+(?:\.\d+)?\r?\n?$",
            Read(".python-version"));

        var workflowsDirectory = Path.Combine(
            RepositoryRoot,
            PathFromRepository(".github/workflows"));
        var workflows = string.Join(
            Environment.NewLine,
            Directory.GetFiles(workflowsDirectory, "*.yml").Select(File.ReadAllText));
        var dotnetSetupCount = workflows.Split(
            "uses: $/.github/actions/setup-dotnet",
            StringSplitOptions.None).Length - 1;
        var pythonSetupCount = workflows.Split(
            "uses: $/.github/actions/setup-python",
            StringSplitOptions.None).Length - 1;
        var globalJsonInputCount = System.Text.RegularExpressions.Regex.Count(
            workflows,
            @"(?m)^[ \t]+global-json-file: (?:global\.json|tooling/global\.json)\r?$");
        var pythonVersionFileInputCount = System.Text.RegularExpressions.Regex.Count(
            workflows,
            @"(?m)^[ \t]+python-version-file: (?:\.python-version|tooling/\.python-version)\r?$");

        Assert.True(dotnetSetupCount > 0, "No setup-dotnet steps were found.");
        Assert.True(pythonSetupCount > 0, "No setup-python steps were found.");
        Assert.Equal(dotnetSetupCount, globalJsonInputCount);
        Assert.Equal(pythonSetupCount, pythonVersionFileInputCount);
        Assert.DoesNotContain("dotnet-version:", workflows, StringComparison.Ordinal);
        Assert.DoesNotContain("python-version:", workflows, StringComparison.Ordinal);

        Assert.Contains("      - '.python-version'", Read(".github/workflows/macos-adhoc-package.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryOwnedDependencyVersionsHaveSingleCodeOwners()
    {
        var workflowSources = string.Join(
            Environment.NewLine,
            Directory.GetFiles(
                Path.Combine(RepositoryRoot, ".github", "workflows"),
                "*.yml").Select(File.ReadAllText));
        var buildProperties = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var targetFrameworkOwner = Assert.Single(
            buildProperties.Descendants(),
            element => element.Name.LocalName == "DownKyiTargetFramework");
        var targetFramework = targetFrameworkOwner.Value.Trim();
        Assert.Matches(@"^net\d+\.\d+(?:-[A-Za-z0-9.-]+)?$", targetFramework);
        var targetFrameworkConsumer = Assert.Single(
            buildProperties.Descendants(),
            element => element.Name.LocalName == "TargetFramework");
        Assert.Equal("$(DownKyiTargetFramework)", targetFrameworkConsumer.Value.Trim());

        var projectTargetFrameworkDeclarations = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .Select(element => Path.GetRelativePath(RepositoryRoot, path)))
            .ToArray();
        Assert.Empty(projectTargetFrameworkDeclarations);
        Assert.DoesNotContain(targetFramework, Read("docs/testing/test-runner-policy.json"), StringComparison.Ordinal);
        Assert.DoesNotContain(targetFramework, Read("script/test-project-runner.ps1"), StringComparison.Ordinal);
        Assert.DoesNotContain(targetFramework, Read("script/macos/package.sh"), StringComparison.Ordinal);
        Assert.DoesNotContain(targetFramework, workflowSources, StringComparison.Ordinal);

        var packages = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        var avaloniaVersionOwner = Assert.Single(
            packages.Descendants(),
            element => element.Name.LocalName == "AvaloniaRuntimeVersion");
        Assert.Matches(@"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$", avaloniaVersionOwner.Value.Trim());
        string[] avaloniaRuntimePackages =
        [
            "Avalonia",
            "Avalonia.Desktop",
            "Avalonia.Headless",
            "Avalonia.Headless.XUnit",
            "Avalonia.Themes.Fluent"
        ];
        foreach (var package in avaloniaRuntimePackages)
        {
            var packageVersion = Assert.Single(
                packages.Descendants(),
                element => element.Name.LocalName == "PackageVersion" &&
                           (string?)element.Attribute("Include") == package);
            Assert.Equal("$(AvaloniaRuntimeVersion)", (string?)packageVersion.Attribute("Version"));
        }

        using var toolManifest = JsonDocument.Parse(Read(".config/dotnet-tools.json"));
        var pupnetVersion = toolManifest.RootElement
            .GetProperty("tools")
            .GetProperty("kuiperzone.pupnet")
            .GetProperty("version")
            .GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$", pupnetVersion);
        Assert.DoesNotMatch(@"dotnet tool install[^\r\n]+--version\s+\d", workflowSources);
        Assert.Contains(".config/dotnet-tools.json", Read("script/install-pupnet.ps1"), StringComparison.Ordinal);

        using var externalAssets = JsonDocument.Parse(Read("script/assets/external-assets.json"));
        var appImageTool = externalAssets.RootElement.GetProperty("appimagetool");
        var appImageToolAsset = appImageTool.GetProperty("assets").GetProperty("linux-x64");
        Assert.Matches(@"^[0-9a-f]{40}$", appImageTool.GetProperty("version").GetString());
        Assert.Equal("appimagetool-x86_64.AppImage", appImageToolAsset.GetProperty("fileName").GetString());
        Assert.Matches(@"^[0-9a-f]{64}$", appImageToolAsset.GetProperty("sha256").GetString());
        Assert.StartsWith(
            "https://github.com/crazysmile-PhD/downkyi-runtime-assets/releases/download/appimagetool-",
            appImageToolAsset.GetProperty("url").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("external-assets.json", Read("script/install-appimagetool.ps1"), StringComparison.Ordinal);
        Assert.Contains("install-appimagetool.ps1", workflowSources, StringComparison.Ordinal);

        Assert.DoesNotMatch(@"\.nuget/packages/grpc\.tools/\d", workflowSources);
        Assert.Contains("-getProperty:PkgGrpc_Tools", workflowSources, StringComparison.Ordinal);

        (string LocalName, string Upstream)[] actionOwners =
        [
            ("actionlint", "raven-actions/actionlint"),
            ("checkout", "actions/checkout"),
            ("download-artifact", "actions/download-artifact"),
            ("import-codesign-certs", "apple-actions/import-codesign-certs"),
            ("release", "ncipollo/release-action"),
            ("setup-dotnet", "actions/setup-dotnet"),
            ("setup-python", "actions/setup-python"),
            ("upload-artifact", "actions/upload-artifact")
        ];
        foreach (var (localName, upstream) in actionOwners)
        {
            var ownerSource = Read($".github/actions/{localName}/action.yml");
            Assert.Single(Regex.Matches(
                ownerSource,
                $@"(?m)^\s*uses:\s*{Regex.Escape(upstream)}@\S+\s*$"));
            Assert.Contains($"uses: $/.github/actions/{localName}", workflowSources, StringComparison.Ordinal);
            Assert.DoesNotContain($"uses: {upstream}@", workflowSources, StringComparison.Ordinal);
        }

        var repeatedDirectActionFamilies = Regex.Matches(
                workflowSources,
                @"(?m)^\s*uses:\s*(?<family>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?:/[^@\s]+)?@(?<version>\S+)\s*$")
            .Cast<Match>()
            .GroupBy(match => match.Groups["family"].Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();
        var codeQlFamily = Assert.Single(repeatedDirectActionFamilies);
        Assert.Equal("github/codeql-action", codeQlFamily.Key);
        Assert.Single(codeQlFamily.Select(match => match.Groups["version"].Value).Distinct(StringComparer.Ordinal));

        var dependabot = Read(".github/dependabot.yml");
        Assert.Contains("directories:", dependabot, StringComparison.Ordinal);
        Assert.Contains("      - /.github/actions/*", dependabot, StringComparison.Ordinal);
        var actionlint = Read(".github/actionlint.yaml");
        Assert.Contains("GitHub's recommended same-repository $/ action syntax", actionlint, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationAutomaticallyValidatesChanges()
    {
        var qualityWorkflow = Read(".github/workflows/quality.yml");

        Assert.Contains("pull_request:", qualityWorkflow, StringComparison.Ordinal);
        var qualityPullRequestTrigger = Slice(qualityWorkflow, "  pull_request:", "  push:");
        Assert.DoesNotContain("paths:", qualityPullRequestTrigger, StringComparison.Ordinal);
        Assert.Contains("windows-latest", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("vars.UBUNTU_X64_RUNNER", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("macos-latest", qualityWorkflow, StringComparison.Ordinal);
        var normalizedQualityWorkflow = qualityWorkflow.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "- name: Upload local-action round-trip fixture\n        uses: $/.github/actions/upload-artifact",
            normalizedQualityWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "- name: Download local-action round-trip fixture\n        uses: $/.github/actions/download-artifact",
            normalizedQualityWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("- name: Verify local-action round trip", normalizedQualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("--no-incremental", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("AnalysisMode=All", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("./script/test-solution.ps1", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("-ExcludeCiInfrastructure", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("Validate targeted resource forensics", qualityWorkflow, StringComparison.Ordinal);
        Assert.Contains("classify-resource-contention.ps1", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("apt-get", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("rpm2cpio", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("package-audit:", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--vulnerable", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--deprecated", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("LogFileName=test-results-${{ matrix.os }}.trx", qualityWorkflow, StringComparison.Ordinal);

        var dependencyAuditWorkflow = Read(".github/workflows/dependency-audit.yml");
        Assert.Contains("pull_request:", dependencyAuditWorkflow, StringComparison.Ordinal);
        var dependencyAuditPullRequestTrigger = Slice(
            dependencyAuditWorkflow,
            "  pull_request:",
            "  push:");
        Assert.DoesNotContain("paths:", dependencyAuditPullRequestTrigger, StringComparison.Ordinal);
        Assert.Contains("schedule:", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("    name: Dependency policy", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("uses: $/.github/actions/setup-dotnet", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("global-json-file: global.json", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("uses: $/.github/actions/setup-python", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("python-version-file: .python-version", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("--vulnerable", dependencyAuditWorkflow, StringComparison.Ordinal);
        Assert.Contains("--include-transitive", dependencyAuditWorkflow, StringComparison.Ordinal);
        var deprecatedStep = Slice(
            dependencyAuditWorkflow,
            "      - name: Deprecated package audit",
            "      - name: Upload failure report");
        Assert.DoesNotContain("if:", deprecatedStep, StringComparison.Ordinal);
        Assert.Contains("--deprecated", deprecatedStep, StringComparison.Ordinal);

        var dependabotConfiguration = Read(".github/dependabot.yml");
        Assert.Contains("package-ecosystem: nuget", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: dotnet-sdk", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("package-ecosystem: github-actions", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("      - /.github/actions/*", dependabotConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-ecosystem-groups:", dependabotConfiguration, StringComparison.Ordinal);
        Assert.DoesNotContain("multi-ecosystem-group:", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("      avalonia-runtime:", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("          - Avalonia.Desktop", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("          - Avalonia.Headless.XUnit", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("time: '00:00'", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Contains("timezone: Etc/UTC", dependabotConfiguration, StringComparison.Ordinal);
        Assert.Equal(
            3,
            dependabotConfiguration.Split("interval: daily", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            3,
            dependabotConfiguration.Split("time: '00:00'", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            3,
            dependabotConfiguration.Split("timezone: Etc/UTC", StringSplitOptions.None).Length - 1);

        var dependabotAutoMergeWorkflow = Read(".github/workflows/dependabot-auto-merge.yml");
        Assert.Contains("pull_request:", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.Contains("contents: write", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.Contains("pull-requests: write", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "github.event.pull_request.user.login == 'dependabot[bot]'",
            dependabotAutoMergeWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "github.event.pull_request.base.ref == github.event.repository.default_branch",
            dependabotAutoMergeWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("gh pr merge --auto --merge", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch-metadata", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("semver-", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout", dependabotAutoMergeWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target:", dependabotAutoMergeWorkflow, StringComparison.Ordinal);

        var ciInfrastructureWorkflow = Read(".github/workflows/ci-infrastructure.yml");
        Assert.Contains("pull_request:", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("paths:", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Equal(
            2,
            ciInfrastructureWorkflow.Split("'tests/Directory.Build.props'", StringSplitOptions.None).Length - 1);
        Assert.Contains("tools/DownKyi.CentralTestRunner/**", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("tools/DownKyi.ProcessSupervision/**", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("./script/test-ci-infrastructure.ps1", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("vars.UBUNTU_X64_RUNNER", ciInfrastructureWorkflow, StringComparison.Ordinal);
        Assert.Contains("macos-latest", ciInfrastructureWorkflow, StringComparison.Ordinal);

        var testScript = Read("script/test-solution.ps1");
        Assert.Contains("Invoke-DownKyiTestSolution", testScript, StringComparison.Ordinal);
        Assert.Contains("Get-DownKyiCiInfrastructureTestClassNames", testScript, StringComparison.Ordinal);
        Assert.Contains("exit $result.ExitCode", testScript, StringComparison.Ordinal);

        var runnerScript = Read("script/test-project-runner.ps1");
        Assert.Contains("DownKyi.CentralTestRunner.csproj", runnerScript, StringComparison.Ordinal);
        Assert.Contains("-nodeReuse:false", runnerScript, StringComparison.Ordinal);
        Assert.Contains("-p:UseSharedCompilation=false", runnerScript, StringComparison.Ordinal);
        Assert.Contains("run-project", runnerScript, StringComparison.Ordinal);
        Assert.Contains("run-solution", runnerScript, StringComparison.Ordinal);

        var resourceClassifier = Read("script/classify-resource-contention.ps1");
        Assert.Contains("$matchedSignatures.Count -eq 0", resourceClassifier, StringComparison.Ordinal);
        Assert.Contains("TargetedResourceContention", resourceClassifier, StringComparison.Ordinal);
        Assert.DoesNotContain("wpr.exe", resourceClassifier, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyCaManualAuditReportsCompleteInventoryWhileReviewedRulesBlockStrictBuild()
    {
        var blockingRules = new[]
        {
            "CA1005", "CA1017", "CA1021", "CA1045", "CA1060",
            "CA1502", "CA1505", "CA1509"
        };
        var advisoryOnlyRules = new[] { "CA1501", "CA1506" };
        var inventoryRules = blockingRules.Concat(advisoryOnlyRules);
        var globalConfig = Read("script/code-metrics/legacy-ca.globalconfig");
        foreach (var rule in inventoryRules)
        {
            Assert.Contains($"dotnet_diagnostic.{rule}.severity = warning", globalConfig, StringComparison.Ordinal);
            Assert.DoesNotContain($"dotnet_diagnostic.{rule}.severity = error", globalConfig, StringComparison.Ordinal);
        }

        var blockingConfig = Read("script/code-metrics/legacy-ca-blocking.globalconfig");
        foreach (var rule in blockingRules)
        {
            Assert.Contains($"dotnet_diagnostic.{rule}.severity = error", blockingConfig, StringComparison.Ordinal);
            Assert.DoesNotContain($"dotnet_diagnostic.{rule}.severity = warning", blockingConfig, StringComparison.Ordinal);
        }

        foreach (var rule in advisoryOnlyRules)
        {
            Assert.DoesNotContain($"dotnet_diagnostic.{rule}.severity = error", blockingConfig, StringComparison.Ordinal);
        }

        var directoryBuildProps = Read("Directory.Build.props");
        Assert.Contains("'$(DownKyiLegacyCaAudit)' != 'true'", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("'$(DownKyiLegacyCaAudit)' == 'true'", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("DownKyiLegacyCaAuditSarifDirectory", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("$(MSBuildProjectName).sarif", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("script/code-metrics/legacy-ca-blocking.globalconfig", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("script/code-metrics/legacy-ca.globalconfig", directoryBuildProps, StringComparison.Ordinal);

        var auditScript = Read("script/audit-code-metrics.ps1");
        Assert.Contains("-p:DownKyiLegacyCaAudit=true", auditScript, StringComparison.Ordinal);
        Assert.Contains("-p:TreatWarningsAsErrors=false", auditScript, StringComparison.Ordinal);
        Assert.Contains("-p:CodeAnalysisTreatWarningsAsErrors=false", auditScript, StringComparison.Ordinal);
        Assert.Contains("raw-build.log", auditScript, StringComparison.Ordinal);
        Assert.Contains("$redactedBuildLines", auditScript, StringComparison.Ordinal);
        Assert.Contains("'<repo>'", auditScript, StringComparison.Ordinal);
        Assert.Contains("report-legacy-ca.ps1", auditScript, StringComparison.Ordinal);
        Assert.Contains("legacy-ca-baseline.json", auditScript, StringComparison.Ordinal);
        Assert.Contains("exit $LASTEXITCODE", auditScript, StringComparison.Ordinal);

        var reportScript = Read("script/code-metrics/report-legacy-ca.ps1");
        Assert.Contains("sourceResult", reportScript, StringComparison.Ordinal);
        Assert.Contains("rawFindingIds", reportScript, StringComparison.Ordinal);
        Assert.Contains("metric-worsened", reportScript, StringComparison.Ordinal);

        var qualityWorkflow = Read(".github/workflows/quality.yml");
        Assert.DoesNotContain("legacy-ca-audit:", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("./script/audit-code-metrics.ps1", qualityWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-ca-code-metrics", qualityWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuousIntegrationBoundsEveryTestJobWithoutRetryingTimeouts()
    {
        var qualityWorkflow = Read(".github/workflows/quality.yml");
        var buildTestStart = qualityWorkflow.IndexOf("  build-test:", StringComparison.Ordinal);
        Assert.True(buildTestStart >= 0, "Could not find the build-test job.");
        var buildTest = qualityWorkflow[buildTestStart..];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            buildTest,
            @"(?m)^    timeout-minutes: 20\r?$"));
        Assert.Contains("fail-fast: false", buildTest, StringComparison.Ordinal);
        Assert.Contains("runner: windows-latest\n            check_name: windows", buildTest.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("runner: ${{ vars.UBUNTU_X64_RUNNER }}\n            check_name: ubuntu-x64", buildTest.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("runner: macos-latest\n            check_name: macos", buildTest.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(
            "- name: Upload test results\n        if: always()\n        continue-on-error: true\n        uses: $/.github/actions/upload-artifact",
            buildTest.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"(?m)^\s+needs\s*:", System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            qualityWorkflow);

        AssertTestJobsUseTwentyMinuteLimit();
        Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            PathFromRepository(".github/workflows/retry-timed-out-quality.yml"))));
    }

    [Fact]
    public void KnowledgeStructureHasNavigableEntryPoints()
    {
        AssertPathsExist(
            "README.md",
            "AGENTS.md",
            "ARCHITECTURE.md",
            "docs/design-docs",
            "docs/exec-plans",
            "docs/testing",
            "docs/operations");

        var agentGuide = Read("AGENTS.md");
        Assert.Contains("ARCHITECTURE.md", agentGuide, StringComparison.Ordinal);
        Assert.Contains("DesktopComposition.cs", agentGuide, StringComparison.Ordinal);
        Assert.Contains("docs/refactoring-live-plan.md", agentGuide, StringComparison.Ordinal);
        Assert.Contains("docs/operations/verification-and-rollback.md", agentGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentEntryUsesProgressiveDisclosureAndGitHubProjectOwnsMutableWorkState()
    {
        var agentGuide = Read("AGENTS.md");
        var livePlan = Read("docs/refactoring-live-plan.md");

        Assert.Contains("Progressive Disclosure Map", agentGuide, StringComparison.Ordinal);
        Assert.Contains(WorkManagementProjectUrl, agentGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("## 強制閱讀順序", agentGuide, StringComparison.Ordinal);

        string[] forbiddenLiveState =
        [
            "Status: active",
            "Last updated:",
            "Current work item:",
            "Current working branch:",
            "Current base:",
            "## Current Item",
            "## Next Items"
        ];

        foreach (var value in forbiddenLiveState)
        {
            Assert.DoesNotContain(value, livePlan, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(
                @"(?m)^\s*-\s+\[[ xX]\]|\b[0-9a-fA-F]{40}\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant),
            livePlan);
        Assert.Contains(WorkManagementProjectUrl, livePlan, StringComparison.Ordinal);

        string[] workManagementEntryPoints =
        [
            "README.md",
            "ARCHITECTURE.md",
            "docs/design-docs/README.md",
            "docs/exec-plans/README.md",
            "docs/maintenance.md",
            "docs/operations/bilibili-api-audit.md"
        ];

        foreach (var path in workManagementEntryPoints)
        {
            Assert.Contains(WorkManagementProjectUrl, Read(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ModificationScopeHasExecutableBoundaryRatchets()
    {
        AssertPathsExist(
            "tests/DownKyi.Architecture.Tests/ProjectDependencyTests.cs",
            "tests/DownKyi.Architecture.Tests/ModuleBoundaryBaselineTests.cs",
            "docs/testing/module-boundary-ratchets.md",
            "script/audit-module-boundaries.ps1");

        var ratchets = Read("tests/DownKyi.Architecture.Tests/ModuleBoundaryBaselineTests.cs");
        Assert.Contains("CoreHasNoUiOrQrRenderingDependencies", ratchets, StringComparison.Ordinal);
        Assert.Contains("ServiceContractsCannotAddPresentationDependencies", ratchets, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureBehaviorPolicyRejectsSilentAndBlockingPatterns()
    {
        AssertPathsExist(
            "tests/DownKyi.Architecture.Tests/LegacyPatternArchitectureTests.cs",
            "tests/DownKyi.Infrastructure.Tests/BilibiliHttpTransportTests.cs",
            "tests/DownKyi.Core.Tests/BiliApiContractSampleTests.cs");

        var policy = Read("tests/DownKyi.Architecture.Tests/LegacyPatternArchitectureTests.cs");
        Assert.Contains("Thread.Sleep", policy, StringComparison.Ordinal);
        Assert.Contains("GetAwaiter.GetResult", policy, StringComparison.Ordinal);
        Assert.Contains("EmptyCatchBlocks", policy, StringComparison.Ordinal);

        var architecture = Read("ARCHITECTURE.md");
        Assert.Contains("typed result", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancellation", architecture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaintenanceWorkflowsAndRollbackAreStandardized()
    {
        AssertPathsExist(
            "docs/maintenance.md",
            "docs/operations/verification-and-rollback.md",
            "script/validate-publish-output.ps1",
            ".github/workflows/build.yml");

        var operations = Read("docs/operations/verification-and-rollback.md");
        Assert.Contains("git revert <commit-sha>", operations, StringComparison.Ordinal);
        Assert.Contains("migration", operations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rollback", operations, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertPathsExist(params string[] relativePaths)
    {
        var missing = relativePaths
            .Where(path => !Path.Exists(Path.Combine(RepositoryRoot, PathFromRepository(path))))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing repository entry points: {string.Join(", ", missing)}");
    }

    private static void AssertTestJobsUseTwentyMinuteLimit()
    {
        var workflowsDirectory = Path.Combine(
            RepositoryRoot,
            PathFromRepository(".github/workflows"));
        var testInvocation = new System.Text.RegularExpressions.Regex(
            @"^\s+(?:\. )?\./(?:tooling/)?script/test-(?:ci-infrastructure|(?:project|solution)(?:-runner)?)\.ps1\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var jobHeader = new System.Text.RegularExpressions.Regex(
            @"^  (?<name>[A-Za-z0-9_-]+):\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var timeout = new System.Text.RegularExpressions.Regex(
            @"^    timeout-minutes: 20\s*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var testJobs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var workflowPath in Directory.GetFiles(workflowsDirectory, "*.yml"))
        {
            var lines = File.ReadAllLines(workflowPath);
            string? currentJob = null;
            var currentJobStart = -1;

            for (var index = 0; index < lines.Length; index++)
            {
                var header = jobHeader.Match(lines[index]);
                if (header.Success)
                {
                    currentJob = header.Groups["name"].Value;
                    currentJobStart = index;
                    continue;
                }

                if (!testInvocation.IsMatch(lines[index]))
                {
                    continue;
                }

                Assert.NotNull(currentJob);
                var jobEnd = Array.FindIndex(
                    lines,
                    currentJobStart + 1,
                    line => jobHeader.IsMatch(line));
                if (jobEnd < 0)
                {
                    jobEnd = lines.Length;
                }

                var jobLines = lines[currentJobStart..jobEnd];
                Assert.Single(jobLines, line => timeout.IsMatch(line));
                testJobs.Add($"{Path.GetFileName(workflowPath)}:{currentJob}");
            }
        }

        Assert.NotEmpty(testJobs);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, PathFromRepository(relativePath)));
    }

    private static string Slice(string value, string startMarker, string endMarker)
    {
        var start = value.IndexOf(startMarker, StringComparison.Ordinal);
        var end = value.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Could not slice from {startMarker} to {endMarker}.");
        return value[start..end];
    }

    private static string PathFromRepository(string path)
    {
        return path.Replace('/', Path.DirectorySeparatorChar);
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
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
