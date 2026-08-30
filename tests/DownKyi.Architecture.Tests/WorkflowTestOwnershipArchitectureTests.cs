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
    public void ReleaseLifecycleRehearsalIsShardedByExactAssemblyOwner()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));

        AssertReleaseLifecycleMatrix(workflow);
    }

    [Fact]
    public void ReleaseLifecycleMatrixCannotDropARequiredAssembly()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, "assembly-lifecycle-release");
        var matrix = RequireMapping(RequireMapping(job, "strategy"), "matrix");
        var assemblies = RequireSequence(matrix, "assembly");
        assemblies.Children.RemoveAt(assemblies.Children.Count - 1);

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecycleMatrix(workflow));
    }

    [Fact]
    public void ReleaseLifecycleArtifactsCannotShareAnOutputOwner()
    {
        var workflow = LoadYaml(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "build.yml"));
        var uploadStep = FindUniqueStep(
            workflow,
            "assembly-lifecycle-release",
            "Upload lifecycle reports and evidence");
        RequireMapping(uploadStep, "with").Children[new YamlScalarNode("name")] =
            new YamlScalarNode("assembly-lifecycle-release-${{ github.run_attempt }}");

        Assert.Throws<InvalidDataException>(() => AssertReleaseLifecycleMatrix(workflow));
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
            RequireMapping(RequireMapping(workflow, "jobs"), "assembly-lifecycle-release")
                .Add("continue-on-error", "true");
        }

        AssertReleaseLifecycleMatrix(workflow);
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

    private static void AssertReleaseLifecycleMatrix(YamlMappingNode workflow)
    {
        var jobs = RequireMapping(workflow, "jobs");
        var job = RequireMapping(jobs, "assembly-lifecycle-release");
        AssertNoBypassControls(job, ForbiddenRequiredJobKeys);
        if (!string.Equals(
                RequireScalar(job, "name"),
                "Assembly lifecycle release gate (${{ matrix.assembly }})",
                StringComparison.Ordinal) ||
            !string.Equals(RequireScalar(job, "runs-on"), "windows-latest", StringComparison.Ordinal) ||
            !string.Equals(RequireScalar(job, "timeout-minutes"), "180", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The release lifecycle shards must retain their Windows runner and 180-minute owner budget.");
        }

        var strategy = RequireMapping(job, "strategy");
        if (strategy.Children.Count != 2 ||
            !string.Equals(RequireScalar(strategy, "fail-fast"), "false", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The release lifecycle strategy must retain all shard evidence after one shard fails.");
        }

        var matrix = RequireMapping(strategy, "matrix");
        if (matrix.Children.Count != 1)
        {
            throw new InvalidDataException(
                "The release lifecycle matrix may contain only the authoritative assembly axis.");
        }

        var actualAssemblies = RequireSequence(matrix, "assembly").Children
            .OfType<YamlScalarNode>()
            .Select(node => node.Value ?? string.Empty)
            .ToArray();
        if (!actualAssemblies.SequenceEqual(RequiredReleaseLifecycleAssemblies, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The release lifecycle matrix must contain every exact Windows-owned test assembly once.");
        }

        var runStep = FindUniqueStep(
            workflow,
            "assembly-lifecycle-release",
            "Run repeated assembly lifecycle gate");
        AssertNoBypassControls(runStep, ["if", "continue-on-error"]);
        if (!string.Equals(RequireScalar(runStep, "shell"), "pwsh", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release lifecycle gate must execute through PowerShell.");
        }

        const string expectedCommand =
            "./script/test-assembly-lifecycle.ps1 " +
            "-Configuration Release " +
            "-Profile Rehearsal " +
            "-NoBuild " +
            "-ValidateForensics " +
            "-AssemblyPattern ${{ matrix.assembly }} " +
            "-ResultsDirectory ./artifacts/assembly-lifecycle/release/${{ matrix.assembly }}";
        if (!string.Equals(
                NormalizeCommand(RequireScalar(runStep, "run")),
                expectedCommand,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Each release lifecycle shard must run the complete Rehearsal profile for its exact assembly.");
        }

        var uploadStep = FindUniqueStep(
            workflow,
            "assembly-lifecycle-release",
            "Upload lifecycle reports and evidence");
        AssertNoBypassControls(uploadStep, ["continue-on-error"]);
        if (!string.Equals(RequireScalar(uploadStep, "if"), "always()", StringComparison.Ordinal) ||
            !string.Equals(RequireScalar(uploadStep, "uses"), "actions/upload-artifact@v7", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Every release lifecycle shard must publish evidence even after failure.");
        }

        var uploadInputs = RequireMapping(uploadStep, "with");
        if (!string.Equals(
                RequireScalar(uploadInputs, "name"),
                "assembly-lifecycle-release-${{ matrix.assembly }}-${{ github.run_attempt }}",
                StringComparison.Ordinal) ||
            !string.Equals(
                RequireScalar(uploadInputs, "path"),
                "artifacts/assembly-lifecycle/release/${{ matrix.assembly }}",
                StringComparison.Ordinal) ||
            !string.Equals(RequireScalar(uploadInputs, "if-no-files-found"), "error", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Release lifecycle shard artifacts must have exact, isolated owners.");
        }

        var changelog = RequireMapping(jobs, "changelog");
        var expectedNeeds = new[]
        {
            "external-assets-preflight",
            "release-gate",
            "assembly-lifecycle-release"
        };
        var actualNeeds = RequireSequence(changelog, "needs").Children
            .OfType<YamlScalarNode>()
            .Select(node => node.Value ?? string.Empty)
            .ToArray();
        const string expectedCondition =
            "${{ always() && needs.external-assets-preflight.result == 'success' && " +
            "needs.release-gate.result == 'success' && " +
            "needs.assembly-lifecycle-release.result == 'success' }}";
        if (!actualNeeds.SequenceEqual(expectedNeeds, StringComparer.Ordinal) ||
            !string.Equals(RequireScalar(changelog, "if"), expectedCondition, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Downstream release work must wait for every release lifecycle shard to succeed.");
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
