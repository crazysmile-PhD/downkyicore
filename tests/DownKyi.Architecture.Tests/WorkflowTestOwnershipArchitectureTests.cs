using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace DownKyi.Architecture.Tests;

public sealed class WorkflowTestOwnershipArchitectureTests
{
    private const string TestSolutionAction = "./.github/actions/test-solution";
    private const string TestProjectAction = "./.github/actions/test-project";
    private const string TestActionScript = "../../../script/invoke-ci-test-action.ps1";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] ForbiddenWorkflowGateKeys = ["run", "if", "continue-on-error"];
    private static readonly string[] ForbiddenActionStepKeys = ["if", "continue-on-error"];
    private static readonly string[] ForbiddenRequiredJobKeys = ["if", "continue-on-error"];
    private static readonly string[] ForbiddenRequiredSuiteJobKeys = ["if", "continue-on-error", "needs"];
    private static readonly string[] RequiredReleaseLifecycleAssemblies =
    [
        "DownKyi.Application.Tests",
        "DownKyi.Architecture.Tests",
        "DownKyi.Core.Tests",
        "DownKyi.Desktop.Tests",
        "DownKyi.Domain.Tests",
        "DownKyi.Infrastructure.Tests",
        "DownKyi.Tests",
        "DownKyi.Windows.Tests"
    ];
    private static readonly string[] StandardReleaseLifecycleAssemblies =
    [
        "DownKyi.Application.Tests",
        "DownKyi.Core.Tests",
        "DownKyi.Desktop.Tests",
        "DownKyi.Domain.Tests",
        "DownKyi.Infrastructure.Tests",
        "DownKyi.Tests"
    ];
    private static readonly string[] ArchitecturePreflightClasses =
    [
        "DownKyi.Architecture.Tests.WorkflowTestOwnershipArchitectureTests",
        "DownKyi.Architecture.Tests.AssemblyLifecycleArchitectureTests",
        "DownKyi.Architecture.Tests.AssemblyLifecycleReleaseEvidenceTests",
        "DownKyi.Architecture.Tests.TestRunnerPolicyArchitectureTests"
    ];
    private static readonly string[] WindowsPreflightClasses =
    [
        "DownKyi.Windows.Tests.AriaServerWindowsTests",
        "DownKyi.ProcessSupervision.Tests.TransitionBudgetTests",
        "DownKyi.ProcessSupervision.Tests.DiagnosticCollectorWindowTests"
    ];
    private const string ReleaseReadyLabel = "assembly-lifecycle-release-ready";
    private const string ExactLifecycleSha = "${{ github.event.pull_request.head.sha || github.sha }}";
    private const string ReleaseReadyCondition =
        "${{ !inputs.update_ffmpeg_assets && (github.event_name != 'pull_request' || " +
        "contains(github.event.pull_request.labels.*.name, 'assembly-lifecycle-release-ready')) }}";
    private const string ReleaseEvidenceCondition =
        "${{ always() && !cancelled() && !inputs.update_ffmpeg_assets && " +
        "(github.event_name != 'pull_request' || " +
        "contains(github.event.pull_request.labels.*.name, 'assembly-lifecycle-release-ready')) }}";

    [Theory]
    [InlineData(".github/workflows/quality.yml", "build-test", "windows-latest,ubuntu-latest,macos-latest")]
    [InlineData(".github/workflows/build.yml", "release-gate", "windows-latest,ubuntu-latest,macos-15")]
    public void RequiredRepositorySuiteIsOwnedByTheStructuredTestAction(
        string workflowPath,
        string jobName,
        string expectedRunners)
    {
        ArgumentNullException.ThrowIfNull(expectedRunners);
        var workflow = LoadYaml(Path.Combine(RepositoryRoot, workflowPath));

        AssertRequiredSuiteGate(workflow, jobName, expectedRunners.Split(','));
    }

    [Theory]
    [InlineData(
        ".github/workflows/quality.yml",
        "build-test",
        "Enforce architecture policy",
        TestProjectAction,
        "./tests/DownKyi.Architecture.Tests/DownKyi.Architecture.Tests.csproj",
        null)]
    [InlineData(
        ".github/workflows/quality.yml",
        "aria2-tls-security",
        "Verify packaged aria2 TLS behavior",
        TestProjectAction,
        "./tests/DownKyi.Tests/DownKyi.Tests.csproj",
        "DownKyi.Tests.Aria2TlsIntegrationTests")]
    [InlineData(
        ".github/workflows/build.yml",
        "build-macos",
        "Run macOS packaging regressions",
        TestProjectAction,
        "./tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj",
        null)]
    [InlineData(
        ".github/workflows/release-v112-recovery.yml",
        "build-macos",
        "Run native recovery tooling regressions",
        "./tooling/.github/actions/test-project",
        "./tests/DownKyi.MacOS.Tests/DownKyi.MacOS.Tests.csproj",
        null)]
    public void RequiredProjectGateIsOwnedByTheStructuredTestAction(
        string workflowPath,
        string jobName,
        string stepName,
        string actionPath,
        string projectPath,
        string? expectedClass)
    {
        var workflow = LoadYaml(Path.Combine(RepositoryRoot, workflowPath));

        AssertRequiredProjectGate(
            workflow,
            jobName,
            stepName,
            actionPath,
            projectPath,
            expectedClass);
    }

    [Fact]
    public void ExpressionSelectedExecutableCannotReplaceTheRequiredSuiteGate()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "quality.yml"));
        var step = FindUniqueStep(workflow, "build-test", "Test");
        step.Children.Remove(new YamlScalarNode("uses"));
        step.Add(
            "run",
            "${{ env.CLI }} ${{ env.VERB }} ${{ env.TEST_DLL }} || true");

        Assert.Throws<InvalidDataException>(() =>
            AssertRequiredSuiteGate(
                workflow,
                "build-test",
                ["windows-latest", "ubuntu-latest", "macos-latest"]));
    }

    [Fact]
    public void ExpressionSelectedExecutableCannotReplaceARequiredProjectGate()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "quality.yml"));
        var step = FindUniqueStep(
            workflow,
            "aria2-tls-security",
            "Verify packaged aria2 TLS behavior");
        step.Children.Remove(new YamlScalarNode("uses"));
        step.Add(
            "run",
            "${{ env.CLI }} ${{ env.VERB }} ${{ env.TEST_DLL }} || true");

        Assert.Throws<InvalidDataException>(() =>
            AssertRequiredProjectGate(
                workflow,
                "aria2-tls-security",
                "Verify packaged aria2 TLS behavior",
                TestProjectAction,
                "./tests/DownKyi.Tests/DownKyi.Tests.csproj",
                "DownKyi.Tests.Aria2TlsIntegrationTests"));
    }

    [Fact]
    public void RequiredSuiteOwnerJobCannotBeConditionallySkipped()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "quality.yml"));
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, "build-test");
        job.Add("if", "${{ false }}");

        Assert.Throws<InvalidDataException>(() =>
            AssertRequiredSuiteGate(
                workflow,
                "build-test",
                ["windows-latest", "ubuntu-latest", "macos-latest"]));
    }

    [Fact]
    public void RequiredSuiteOwnerJobCannotDependOnASkippableJob()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var jobs = RequireMapping(workflow, "jobs");
        RequireMapping(jobs, "release-gate").Add("needs", "external-assets-preflight");

        Assert.Throws<InvalidDataException>(() =>
            AssertRequiredSuiteGate(
                workflow,
                "release-gate",
                ["windows-latest", "ubuntu-latest", "macos-15"]));
    }

    [Fact]
    public void RequiredSuiteOwnerRejectsReducedRunnerMatrix()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "quality.yml"));
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, "build-test");
        var runners = RequireSequence(RequireMapping(RequireMapping(job, "strategy"), "matrix"), "os");
        runners.Children.RemoveAt(runners.Children.Count - 1);

        Assert.Throws<InvalidDataException>(() =>
            AssertRequiredSuiteGate(
                workflow,
                "build-test",
                ["windows-latest", "ubuntu-latest", "macos-latest"]));
    }

    [Fact]
    public void RequiredSuiteSchedulingMutationProfileFailsClosed()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOWNKYI_TEST_MUTATE_CENTRAL_REQUIRED_SUITE_SCHEDULING"),
                "1",
                StringComparison.Ordinal))
        {
            RequireMapping(RequireMapping(workflow, "jobs"), "release-gate")
                .Add("needs", "external-assets-preflight");
        }

        AssertRequiredSuiteGate(
            workflow,
            "release-gate",
            ["windows-latest", "ubuntu-latest", "macos-15"]);
    }

    [Fact]
    public void ReleaseLifecycleUsesLockPreflightAndExactHundredRoundWaves()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));

        AssertReleaseLifecyclePolicy(workflow);
    }

    [Fact]
    public void ReleaseLifecycleStandardWaveCannotDropARequiredAssembly()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, "assembly-lifecycle-release-standard");
        var matrix = RequireMapping(RequireMapping(job, "strategy"), "matrix");
        var assemblies = RequireSequence(matrix, "assembly");
        assemblies.Children.RemoveAt(assemblies.Children.Count - 1);

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecyclePolicy(workflow));
    }

    [Fact]
    public void ReleaseLifecycleArtifactsCannotShareAnOutputOwner()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var uploadStep = FindUniqueConditionalStep(
            workflow,
            "assembly-lifecycle-release-architecture",
            "Upload Architecture shard evidence");
        RequireMapping(uploadStep, "with").Children[new YamlScalarNode("name")] =
            new YamlScalarNode("assembly-lifecycle-release-architecture-${{ github.run_attempt }}");

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecyclePolicy(workflow));
    }

    [Fact]
    public void OrdinaryPullRequestCannotEnableTheHundredRoundReleaseWaves()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        RequireMapping(RequireMapping(workflow, "jobs"), "assembly-lifecycle-release-architecture")
            .Children[new YamlScalarNode("if")] = new YamlScalarNode("${{ true }}");

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecyclePolicy(workflow));
    }

    [Fact]
    public void ReleaseLifecycleArchitectureWaveCannotDropAShard()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var job = RequireMapping(
            RequireMapping(workflow, "jobs"),
            "assembly-lifecycle-release-architecture");
        var shards = RequireSequence(RequireMapping(RequireMapping(job, "strategy"), "matrix"), "shard");
        shards.Children.RemoveAt(shards.Children.Count - 1);

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecyclePolicy(workflow));
    }

    [Fact]
    public void ReleaseLifecycleAggregatorCannotDropItsMutationSelfTests()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var step = FindUniqueConditionalStep(
            workflow,
            "assembly-lifecycle-release-windows-evidence",
            "Aggregate Windows exact-head evidence");
        step.Children[new YamlScalarNode("run")] = new YamlScalarNode(
            RequireScalar(step, "run").Replace(
                "-ValidateMutationSelfTests",
                string.Empty,
                StringComparison.Ordinal));

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecyclePolicy(workflow));
    }

    [Fact]
    public void ReleaseLifecyclePreflightCannotReplaceARequiredProofClass()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "assembly-lifecycle-release-topology.json"));
        var mutation = source.Replace(
            "DownKyi.Architecture.Tests.TestRunnerPolicyArchitectureTests",
            "DownKyi.Architecture.Tests.WorkflowTestOwnershipArchitectureTests",
            StringComparison.Ordinal);
        using var topology = JsonDocument.Parse(mutation);
        var project = topology.RootElement.GetProperty("preflightProjects")[0];

        Assert.Throws<InvalidDataException>(() => AssertPreflightProject(
            project,
            "DownKyi.Architecture.Tests",
            ArchitecturePreflightClasses));
    }

    [Fact]
    public void ReleaseLifecycleShardFailureCannotBeMaskedMutationProfile()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        if (string.Equals(
                Environment.GetEnvironmentVariable(
                    "DOWNKYI_TEST_MUTATE_RELEASE_LIFECYCLE_SHARDS"),
                "1",
                StringComparison.Ordinal))
        {
            RequireMapping(
                    RequireMapping(workflow, "jobs"),
                    "assembly-lifecycle-release-architecture")
                .Add("continue-on-error", "true");
        }

        AssertReleaseLifecyclePolicy(workflow);
    }

    [Fact]
    public void RecoveryProjectPathResolvesInsideNestedToolingCheckout()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "release-v112-recovery.yml"));
        var step = FindUniqueStep(workflow, "build-macos", "Run native recovery tooling regressions");
        var inputs = RequireMapping(step, "with");
        var repositoryInput = RequireScalar(inputs, "repository-root");
        var projectInput = RequireScalar(inputs, "project-path");
        var workspace = Path.Combine(Path.GetTempPath(), $"downkyi-recovery-path-{Guid.NewGuid():N}");
        try
        {
            var nestedRepository = Path.GetFullPath(repositoryInput, workspace);
            var expectedProject = Path.Combine(
                nestedRepository,
                "tests",
                "DownKyi.MacOS.Tests",
                "DownKyi.MacOS.Tests.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedProject)!);
            File.WriteAllText(expectedProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var options = new DownKyi.CentralTestRunner.CentralTestProjectOptions(
                nestedRepository,
                projectInput,
                "Release",
                noRestore: false,
                noBuild: false,
                resultsDirectory: null,
                trxName: null,
                classNames: null,
                filter: null,
                executionTimeoutSeconds: 30);

            Assert.Equal(Path.GetFullPath(expectedProject), options.ProjectPath);
            Assert.True(File.Exists(options.ProjectPath));
            Assert.DoesNotContain(
                $"tooling{Path.DirectorySeparatorChar}tooling",
                options.ProjectPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public void StructuredTestActionDelegatesToTheCentralSolutionRunner()
    {
        var action = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "actions",
            "test-solution",
            "action.yml"));

        AssertStructuredActionWiring(action, "Solution");
        AssertActionEnvironment(
            action,
            "DOWNKYI_TEST_RESULTS_DIRECTORY",
            "${{ inputs.results-directory }}");
    }

    [Fact]
    public void StructuredProjectActionDelegatesToTheCentralRunnerAndValidator()
    {
        var action = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "actions",
            "test-project",
            "action.yml"));

        AssertStructuredActionWiring(action, "Project");
        var expectedEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOWNKYI_TEST_REPOSITORY_ROOT"] = "${{ inputs.repository-root }}",
            ["DOWNKYI_TEST_PROJECT_PATH"] = "${{ inputs.project-path }}",
            ["DOWNKYI_TEST_CONFIGURATION"] = "${{ inputs.configuration }}",
            ["DOWNKYI_TEST_NO_RESTORE"] = "${{ inputs.no-restore }}",
            ["DOWNKYI_TEST_NO_BUILD"] = "${{ inputs.no-build }}",
            ["DOWNKYI_TEST_RESULTS_DIRECTORY"] = "${{ inputs.results-directory }}",
            ["DOWNKYI_TEST_TRX_NAME"] = "${{ inputs.trx-name }}",
            ["DOWNKYI_TEST_EXPECTED_CLASS"] = "${{ inputs.expected-class }}"
        };
        foreach (var pair in expectedEnvironment)
        {
            AssertActionEnvironment(action, pair.Key, pair.Value);
        }
    }

    [Theory]
    [InlineData("test-solution", "Solution")]
    [InlineData("test-project", "Project")]
    public void StructuredActionCannotAppendAFalseGreenCommand(string actionName, string mode)
    {
        var action = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "actions",
            actionName,
            "action.yml"));
        var step = GetOnlyActionStep(action);
        step.Children[new YamlScalarNode("run")] = new YamlScalarNode(
            RequireScalar(step, "run") + "; exit 0");

        Assert.Throws<InvalidDataException>(() =>
            AssertStructuredActionWiring(action, mode));
    }

    private static void AssertStructuredActionWiring(YamlMappingNode action, string mode)
    {
        var runs = RequireMapping(action, "runs");
        if (!string.Equals(RequireScalar(runs, "using"), "composite", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The repository test action must remain composite.");
        }

        var step = GetOnlyActionStep(action);
        AssertNoBypassControls(step, ForbiddenActionStepKeys);
        if (!string.Equals(RequireScalar(step, "shell"), "pwsh", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The repository test action must use PowerShell.");
        }

        var expected = NormalizeCommand(
            $"& (Join-Path '${{{{ github.action_path }}}}' '{TestActionScript}') -Mode {mode}");
        if (!string.Equals(
                NormalizeCommand(RequireScalar(step, "run")),
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The repository test action must delegate exclusively to {mode} mode.");
        }
    }

    private static void AssertActionEnvironment(
        YamlMappingNode action,
        string variableName,
        string expectedValue)
    {
        var environment = RequireMapping(GetOnlyActionStep(action), "env");
        if (!string.Equals(
                RequireScalar(environment, variableName),
                expectedValue,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Action environment '{variableName}' is not wired to its declared input.");
        }
    }

    private static YamlMappingNode GetOnlyActionStep(YamlMappingNode action)
    {
        var runs = RequireMapping(action, "runs");
        var steps = RequireSequence(runs, "steps");
        return steps.Children.OfType<YamlMappingNode>().Single();
    }

    private static string NormalizeCommand(string command)
    {
        return string.Join(
            " ",
            command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AssertRequiredSuiteGate(
        YamlMappingNode workflow,
        string jobName,
        IReadOnlyList<string> expectedRunners)
    {
        var step = FindUniqueStep(workflow, jobName, "Test");
        AssertStructuredGate(step, TestSolutionAction);
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, jobName);
        AssertNoBypassControls(job, ForbiddenRequiredSuiteJobKeys);
        if (!string.Equals(RequireScalar(job, "runs-on"), "${{ matrix.os }}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The required suite owner must run directly on matrix.os.");
        }

        var matrix = RequireMapping(RequireMapping(job, "strategy"), "matrix");
        if (matrix.Children.Count != 1)
        {
            throw new InvalidDataException(
                "The required suite owner matrix may contain only the authoritative os axis.");
        }

        var actualRunners = RequireSequence(matrix, "os").Children
            .OfType<YamlScalarNode>()
            .Select(node => node.Value ?? string.Empty)
            .ToArray();
        if (!actualRunners.SequenceEqual(expectedRunners, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The required suite owner runner matrix was reduced or changed.");
        }
    }

    private static void AssertRequiredProjectGate(
        YamlMappingNode workflow,
        string jobName,
        string stepName,
        string actionPath,
        string projectPath,
        string? expectedClass)
    {
        var step = FindUniqueStep(workflow, jobName, stepName);
        AssertStructuredGate(step, actionPath);
        var inputs = RequireMapping(step, "with");
        if (!string.Equals(
                RequireScalar(inputs, "project-path"),
                projectPath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The required project gate '{stepName}' has an unexpected project owner.");
        }
        if (expectedClass != null &&
            !string.Equals(
                RequireScalar(inputs, "expected-class"),
                expectedClass,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The required project gate '{stepName}' has an unexpected class proof.");
        }
    }

    private static void AssertReleaseLifecyclePolicy(YamlMappingNode workflow)
    {
        using var topology = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "assembly-lifecycle-release-topology.json")));
        var topologyRoot = topology.RootElement;
        if (topologyRoot.GetProperty("schemaVersion").GetInt32() != 1 ||
            !string.Equals(
                topologyRoot.GetProperty("readyLabel").GetString(),
                ReleaseReadyLabel,
                StringComparison.Ordinal) ||
            !string.Equals(
                topologyRoot.GetProperty("profile").GetString(),
                "Rehearsal",
                StringComparison.Ordinal) ||
            topologyRoot.GetProperty("totalIterations").GetInt32() != 100 ||
            topologyRoot.GetProperty("waveOneMaximumJobs").GetInt32() != 20 ||
            topologyRoot.GetProperty("waveTwoMaximumJobs").GetInt32() != 20)
        {
            throw new InvalidDataException("The release lifecycle topology weakened its fixed authority.");
        }

        var topologyAssemblies = topologyRoot.GetProperty("assemblies")
            .EnumerateArray()
            .Select(item => item.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        if (!topologyAssemblies.SequenceEqual(RequiredReleaseLifecycleAssemblies, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The release lifecycle topology lost an assembly owner.");
        }
        var topologyShardCounts = topologyRoot.GetProperty("assemblies")
            .EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("name").GetString() ?? string.Empty,
                item => item.GetProperty("shardCount").GetInt32(),
                StringComparer.Ordinal);
        if (topologyShardCounts["DownKyi.Architecture.Tests"] != 16 ||
            topologyShardCounts["DownKyi.Windows.Tests"] != 20 ||
            topologyShardCounts.Where(pair =>
                    pair.Key is not "DownKyi.Architecture.Tests" and not "DownKyi.Windows.Tests")
                .Any(pair => pair.Value != 1))
        {
            throw new InvalidDataException("The release lifecycle topology has an invalid shard allocation.");
        }
        var preflightProjects = topologyRoot.GetProperty("preflightProjects")
            .EnumerateArray()
            .ToArray();
        if (preflightProjects.Length != 2)
        {
            throw new InvalidDataException("The lifecycle lock preflight lost a project.");
        }
        AssertPreflightProject(
            preflightProjects[0],
            "DownKyi.Architecture.Tests",
            ArchitecturePreflightClasses);
        AssertPreflightProject(
            preflightProjects[1],
            "DownKyi.Windows.Tests",
            WindowsPreflightClasses);

        var eventMapping = RequireMapping(RequireMapping(workflow, "on"), "pull_request");
        var eventTypes = RequireSequence(eventMapping, "types").Children
            .OfType<YamlScalarNode>()
            .Select(node => node.Value ?? string.Empty)
            .ToArray();
        if (!eventTypes.SequenceEqual(
                ["opened", "synchronize", "reopened", "labeled", "unlabeled"],
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("The explicit release-ready label cannot control PR execution.");
        }
        var concurrency = RequireMapping(workflow, "concurrency");
        if (!string.Equals(
                RequireScalar(concurrency, "group"),
                "downkyi-${{ github.workflow }}-${{ github.event.pull_request.number || github.run_id }}",
                StringComparison.Ordinal) ||
            !string.Equals(
                RequireScalar(concurrency, "cancel-in-progress"),
                "${{ github.event_name == 'pull_request' }}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A newer PR head must cancel stale lifecycle work.");
        }

        var jobs = RequireMapping(workflow, "jobs");
        var preflight = RequireMapping(jobs, "assembly-lifecycle-preflight");
        AssertNoBypassControls(preflight, ["continue-on-error"]);
        AssertScalar(preflight, "if", "${{ !inputs.update_ffmpeg_assets }}");
        AssertScalar(preflight, "runs-on", "windows-latest");
        AssertScalar(preflight, "timeout-minutes", "30");
        AssertCommand(
            FindUniqueConditionalStep(workflow, "assembly-lifecycle-preflight", "Inspect both lifecycle locks once"),
            "./script/test-assembly-lifecycle-lock-preflight.ps1 " +
            $"-ExpectedCommitSha '{ExactLifecycleSha}' " +
            "-ResultsDirectory ./artifacts/assembly-lifecycle/preflight");

        var standard = RequireMapping(jobs, "assembly-lifecycle-release-standard");
        AssertReleaseReadyJob(standard, ["release-gate", "assembly-lifecycle-preflight"]);
        AssertScalar(standard, "timeout-minutes", "180");
        var standardStrategy = RequireMapping(standard, "strategy");
        AssertScalar(standardStrategy, "fail-fast", "false");
        AssertScalar(standardStrategy, "max-parallel", "4");
        var standardAssemblies = RequireSequence(
                RequireMapping(standardStrategy, "matrix"),
                "assembly")
            .Children.OfType<YamlScalarNode>()
            .Select(node => node.Value ?? string.Empty)
            .ToArray();
        if (!standardAssemblies.SequenceEqual(StandardReleaseLifecycleAssemblies, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The standard release lifecycle wave lost an assembly.");
        }
        AssertCommand(
            FindUniqueConditionalStep(
                workflow,
                "assembly-lifecycle-release-standard",
                "Run exact 100-round standard assembly shard"),
            "./script/run-assembly-lifecycle-release-shard.ps1 " +
            "-Assembly ${{ matrix.assembly }} -ShardIndex 0 " +
            $"-ExpectedCommitSha '{ExactLifecycleSha}' " +
            "-ResultsDirectory ./artifacts/assembly-lifecycle/release/${{ matrix.assembly }}/shard-00");

        var architecture = RequireMapping(jobs, "assembly-lifecycle-release-architecture");
        AssertReleaseReadyJob(architecture, ["release-gate", "assembly-lifecycle-preflight"]);
        AssertShardMatrix(architecture, 16, 16);
        AssertCommand(
            FindUniqueConditionalStep(
                workflow,
                "assembly-lifecycle-release-architecture",
                "Run Architecture Rehearsal shard"),
            "./script/run-assembly-lifecycle-release-shard.ps1 " +
            "-Assembly DownKyi.Architecture.Tests -ShardIndex ${{ matrix.shard }} " +
            $"-ExpectedCommitSha '{ExactLifecycleSha}' " +
            "-ResultsDirectory ./artifacts/assembly-lifecycle/release/DownKyi.Architecture.Tests/shard-${{ matrix.shard }}");
        AssertArtifactOwner(
            FindUniqueConditionalStep(
                workflow,
                "assembly-lifecycle-release-architecture",
                "Upload Architecture shard evidence"),
            "assembly-lifecycle-release-architecture-${{ matrix.shard }}-${{ github.run_attempt }}",
            "artifacts/assembly-lifecycle/release/DownKyi.Architecture.Tests/shard-${{ matrix.shard }}");

        var architectureEvidence = RequireMapping(
            jobs,
            "assembly-lifecycle-release-architecture-evidence");
        AssertEvidenceJob(
            workflow,
            architectureEvidence,
            "assembly-lifecycle-release-architecture",
            "assembly-lifecycle-release-architecture-evidence",
            "Aggregate Architecture exact-head evidence",
            "DownKyi.Architecture.Tests",
            "architecture");

        var windows = RequireMapping(jobs, "assembly-lifecycle-release-windows");
        AssertNoBypassControls(windows, ["continue-on-error"]);
        AssertNeeds(
            windows,
            ["assembly-lifecycle-release-standard", "assembly-lifecycle-release-architecture-evidence"]);
        const string windowsCondition =
            "${{ !cancelled() && !inputs.update_ffmpeg_assets && " +
            "needs.assembly-lifecycle-release-standard.result == 'success' && " +
            "needs.assembly-lifecycle-release-architecture-evidence.result == 'success' && " +
            "(github.event_name != 'pull_request' || " +
            "contains(github.event.pull_request.labels.*.name, 'assembly-lifecycle-release-ready')) }}";
        AssertScalar(windows, "if", windowsCondition);
        AssertShardMatrix(windows, 20, 20);
        AssertCommand(
            FindUniqueConditionalStep(
                workflow,
                "assembly-lifecycle-release-windows",
                "Run Windows Rehearsal shard"),
            "./script/run-assembly-lifecycle-release-shard.ps1 " +
            "-Assembly DownKyi.Windows.Tests -ShardIndex ${{ matrix.shard }} " +
            $"-ExpectedCommitSha '{ExactLifecycleSha}' " +
            "-ResultsDirectory ./artifacts/assembly-lifecycle/release/DownKyi.Windows.Tests/shard-${{ matrix.shard }}");
        AssertArtifactOwner(
            FindUniqueConditionalStep(
                workflow,
                "assembly-lifecycle-release-windows",
                "Upload Windows shard evidence"),
            "assembly-lifecycle-release-windows-${{ matrix.shard }}-${{ github.run_attempt }}",
            "artifacts/assembly-lifecycle/release/DownKyi.Windows.Tests/shard-${{ matrix.shard }}");

        var windowsEvidence = RequireMapping(jobs, "assembly-lifecycle-release-windows-evidence");
        AssertEvidenceJob(
            workflow,
            windowsEvidence,
            "assembly-lifecycle-release-windows",
            "assembly-lifecycle-release-windows-evidence",
            "Aggregate Windows exact-head evidence",
            "DownKyi.Windows.Tests",
            "windows");

        var release = RequireMapping(jobs, "assembly-lifecycle-release");
        AssertNoBypassControls(release, ["continue-on-error"]);
        AssertScalar(release, "if", ReleaseEvidenceCondition);
        AssertNeeds(
            release,
            [
                "assembly-lifecycle-release-standard",
                "assembly-lifecycle-release-architecture-evidence",
                "assembly-lifecycle-release-windows-evidence"
            ]);
        var finalStep = FindUniqueConditionalStep(
            workflow,
            "assembly-lifecycle-release",
            "Require every lifecycle release wave");
        AssertNoBypassControls(finalStep, ["if", "continue-on-error"]);
        var finalEnvironment = RequireMapping(finalStep, "env");
        AssertScalar(
            finalEnvironment,
            "STANDARD_RESULT",
            "${{ needs.assembly-lifecycle-release-standard.result }}");
        AssertScalar(
            finalEnvironment,
            "ARCHITECTURE_RESULT",
            "${{ needs.assembly-lifecycle-release-architecture-evidence.result }}");
        AssertScalar(
            finalEnvironment,
            "WINDOWS_RESULT",
            "${{ needs.assembly-lifecycle-release-windows-evidence.result }}");

        var shardRunner = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "invoke-assembly-lifecycle-release-shard.ps1"));
        var evidenceValidator = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "assert-assembly-lifecycle-release-evidence.ps1"));
        Assert.Contains("-Profile Rehearsal", shardRunner, StringComparison.Ordinal);
        Assert.Contains("-ValidateForensics", shardRunner, StringComparison.Ordinal);
        Assert.Contains("$assemblyPattern = $Assembly", shardRunner, StringComparison.Ordinal);
        Assert.DoesNotContain("[Regex]::Escape", shardRunner, StringComparison.Ordinal);
        Assert.DoesNotContain("TotalIterations", shardRunner, StringComparison.Ordinal);
        foreach (var mutation in new[]
                 {
                     "missing-shard",
                     "duplicate-shard",
                     "stale-commit",
                     "wrong-report-hash"
                 })
        {
            Assert.Contains(mutation, evidenceValidator, StringComparison.Ordinal);
        }

        var changelog = RequireMapping(jobs, "changelog");
        AssertNeeds(
            changelog,
            ["external-assets-preflight", "release-gate", "assembly-lifecycle-release"]);
        const string expectedChangelogCondition =
            "${{ always() && needs.external-assets-preflight.result == 'success' && " +
            "needs.release-gate.result == 'success' && " +
            "needs.assembly-lifecycle-release.result == 'success' }}";
        AssertScalar(changelog, "if", expectedChangelogCondition);
    }

    private static void AssertReleaseReadyJob(
        YamlMappingNode job,
        IReadOnlyList<string> expectedNeeds)
    {
        AssertNoBypassControls(job, ["continue-on-error"]);
        AssertScalar(job, "if", ReleaseReadyCondition);
        AssertScalar(job, "runs-on", "windows-latest");
        AssertNeeds(job, expectedNeeds);
    }

    private static void AssertPreflightProject(
        JsonElement project,
        string expectedAssembly,
        IReadOnlyList<string> expectedClasses)
    {
        var assembly = project.GetProperty("assembly").GetString();
        var classes = project.GetProperty("classes")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        if (!string.Equals(assembly, expectedAssembly, StringComparison.Ordinal) ||
            !classes.SequenceEqual(expectedClasses, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The lifecycle lock preflight changed its exact proof set.");
        }
    }

    private static void AssertShardMatrix(
        YamlMappingNode job,
        int expectedShardCount,
        int expectedMaxParallel)
    {
        AssertScalar(job, "runs-on", "windows-latest");
        AssertScalar(job, "timeout-minutes", "30");
        var strategy = RequireMapping(job, "strategy");
        AssertScalar(strategy, "fail-fast", "false");
        AssertScalar(
            strategy,
            "max-parallel",
            expectedMaxParallel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var shards = RequireSequence(RequireMapping(strategy, "matrix"), "shard")
            .Children.OfType<YamlScalarNode>()
            .Select(node => int.Parse(node.Value!, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (!shards.SequenceEqual(Enumerable.Range(0, expectedShardCount)))
        {
            throw new InvalidDataException("The release lifecycle shard range is incomplete.");
        }
    }

    private static void AssertEvidenceJob(
        YamlMappingNode workflow,
        YamlMappingNode job,
        string expectedNeed,
        string jobName,
        string stepName,
        string assembly,
        string pathName)
    {
        AssertNoBypassControls(job, ["continue-on-error"]);
        AssertScalar(job, "if", ReleaseEvidenceCondition);
        AssertNeeds(job, [expectedNeed]);
        AssertScalar(job, "runs-on", "ubuntu-latest");
        var command =
            "./script/assert-assembly-lifecycle-release-evidence.ps1 " +
            $"-EvidenceRoot ./artifacts/assembly-lifecycle/downloaded/{pathName} " +
            $"-ExpectedAssembly {assembly} " +
            $"-ExpectedCommitSha '{ExactLifecycleSha}' " +
            $"-OutputPath ./artifacts/assembly-lifecycle/aggregate/{pathName}/aggregate-manifest.json " +
            "-ValidateMutationSelfTests";
        AssertCommand(FindUniqueConditionalStep(workflow, jobName, stepName), command);
    }

    private static void AssertArtifactOwner(
        YamlMappingNode step,
        string expectedName,
        string expectedPath)
    {
        AssertNoBypassControls(step, ["continue-on-error"]);
        AssertScalar(step, "if", "always()");
        AssertScalar(step, "uses", "actions/upload-artifact@v7");
        var inputs = RequireMapping(step, "with");
        AssertScalar(inputs, "name", expectedName);
        AssertScalar(inputs, "path", expectedPath);
        AssertScalar(inputs, "if-no-files-found", "error");
    }

    private static void AssertCommand(YamlMappingNode step, string expectedCommand)
    {
        AssertNoBypassControls(step, ["if", "continue-on-error"]);
        AssertScalar(step, "shell", "pwsh");
        if (!string.Equals(
                NormalizeCommand(RequireScalar(step, "run")),
                expectedCommand,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("A lifecycle release command changed its authority.");
        }
    }

    private static void AssertNeeds(YamlMappingNode job, IReadOnlyList<string> expectedNeeds)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("needs"), out var value))
        {
            throw new InvalidDataException("A lifecycle release dependency is missing.");
        }
        var actual = value switch
        {
            YamlScalarNode scalar => new[] { scalar.Value ?? string.Empty },
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>()
                .Select(node => node.Value ?? string.Empty)
                .ToArray(),
            _ => throw new InvalidDataException("A lifecycle release dependency is malformed.")
        };
        if (!actual.SequenceEqual(expectedNeeds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("A lifecycle release dependency was bypassed.");
        }
    }

    private static void AssertScalar(YamlMappingNode mapping, string key, string expected)
    {
        if (!string.Equals(RequireScalar(mapping, key), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Expected exact lifecycle scalar '{key}'.");
        }
    }

    private static void AssertStructuredGate(YamlMappingNode step, string actionPath)
    {
        var uses = RequireScalar(step, "uses");
        if (!string.Equals(uses, actionPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The required repository test gate must use {actionPath}.");
        }

        AssertNoBypassControls(step, ForbiddenWorkflowGateKeys);
    }

    private static void AssertNoBypassControls(
        YamlMappingNode node,
        IEnumerable<string> forbiddenKeys)
    {
        foreach (var forbiddenKey in forbiddenKeys)
        {
            if (node.Children.ContainsKey(new YamlScalarNode(forbiddenKey)))
            {
                throw new InvalidDataException(
                    $"The required repository suite gate cannot declare '{forbiddenKey}'.");
            }
        }
    }

    private static YamlMappingNode FindUniqueConditionalStep(
        YamlMappingNode workflow,
        string jobName,
        string stepName)
    {
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, jobName);
        AssertNoBypassControls(job, ["continue-on-error"]);
        var steps = RequireSequence(job, "steps");
        var matches = steps.Children
            .OfType<YamlMappingNode>()
            .Where(step =>
                step.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
                string.Equals(
                    ((YamlScalarNode)name).Value,
                    stepName,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Job '{jobName}' must contain exactly one '{stepName}' step.");
        }

        return matches[0];
    }

    private static YamlMappingNode FindUniqueStep(
        YamlMappingNode workflow,
        string jobName,
        string stepName)
    {
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, jobName);
        AssertNoBypassControls(job, ForbiddenRequiredJobKeys);
        var steps = RequireSequence(job, "steps");
        var matches = steps.Children
            .OfType<YamlMappingNode>()
            .Where(step =>
                step.Children.TryGetValue(new YamlScalarNode("name"), out var name) &&
                string.Equals(
                    ((YamlScalarNode)name).Value,
                    stepName,
                    StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException(
                $"Job '{jobName}' must contain exactly one '{stepName}' step.");
        }

        return matches[0];
    }

    private static YamlMappingNode LoadYaml(string path)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);
        if (yaml.Documents.Count != 1 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException($"Expected one YAML mapping document: {path}");
        }

        return root;
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key)
    {
        return parent.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
               value is YamlMappingNode mapping
            ? mapping
            : throw new InvalidDataException($"Expected YAML mapping '{key}'.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return parent.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
               value is YamlSequenceNode sequence
            ? sequence
            : throw new InvalidDataException($"Expected YAML sequence '{key}'.");
    }

    private static string RequireScalar(YamlMappingNode parent, string key)
    {
        return parent.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
               value is YamlScalarNode scalar &&
               !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value
            : throw new InvalidDataException($"Expected YAML scalar '{key}'.");
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
