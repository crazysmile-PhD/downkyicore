using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DownKyi.CentralTestRunner;

namespace DownKyi.Architecture.Tests;

public sealed class CiEvidenceAggregatorBehaviorTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ExpectedSha = new('a', 40);

    [Fact]
    public void CompleteDistributedEvidenceIsAccepted()
    {
        using var fixture = CiEvidenceFixture.Create();

        var result = CiEvidenceAggregator.Validate(fixture.CreateOptions());

        Assert.Equal(4, result.BuildCount);
        Assert.Equal(6, result.RepositoryShardCount);
        Assert.Equal(24, result.RepositoryProjectCount);
        Assert.Equal(12, result.ReviewShardCount);
        Assert.Equal(fixture.ProofCount * 3, result.ReviewProofCount);
        Assert.Equal(8, result.LifecycleAssemblyCount);
    }

    [Fact]
    public void MissingLifecycleGateEvidenceFailsClosed()
    {
        using var fixture = CiEvidenceFixture.Create();
        fixture.RemoveLifecycleGateEvidence();

        Assert.Throws<InvalidDataException>(() =>
            CiEvidenceAggregator.Validate(fixture.CreateOptions()));
    }

    [Fact]
    public void LifecycleGateEvidenceFromSecondAssemblyFailsClosed()
    {
        using var fixture = CiEvidenceFixture.Create();
        fixture.CopyLifecycleGateEvidenceToNonAuthority();

        Assert.Throws<InvalidDataException>(() =>
            CiEvidenceAggregator.Validate(fixture.CreateOptions()));
    }

    [Theory]
    [InlineData("delete-shard", "DOWNKYI_TEST_MUTATE_CI_DELETE_SHARD")]
    [InlineData("duplicate-shard", "DOWNKYI_TEST_MUTATE_CI_DUPLICATE_SHARD")]
    [InlineData("wrong-sha", "DOWNKYI_TEST_MUTATE_CI_WRONG_SHA")]
    [InlineData("malformed-evidence", "DOWNKYI_TEST_MUTATE_CI_MALFORMED_EVIDENCE")]
    [InlineData("zero-executed", "DOWNKYI_TEST_MUTATE_CI_ZERO_EXECUTED")]
    [InlineData("skipped-required-shard", "DOWNKYI_TEST_MUTATE_CI_SKIPPED_SHARD")]
    [InlineData("aggregator-false-green", "DOWNKYI_TEST_MUTATE_CI_AGGREGATOR_FALSE_GREEN")]
    [InlineData("unexpected-cardinality", "DOWNKYI_TEST_MUTATE_CI_FAILURE_CARDINALITY")]
    [InlineData("missing-lifecycle-iteration", "DOWNKYI_TEST_MUTATE_CI_LIFECYCLE_ITERATION")]
    public void DistributedEvidenceMutationsFailClosed(string scenario, string mutationVariable)
    {
        using var fixture = CiEvidenceFixture.Create();
        fixture.ApplyScenario(scenario);
        var mutation = string.Equals(
            Environment.GetEnvironmentVariable(mutationVariable),
            "1",
            StringComparison.Ordinal)
            ? CiEvidenceAggregatorMutation.ReturnFalseGreen
            : CiEvidenceAggregatorMutation.None;

        Assert.ThrowsAny<Exception>(() =>
            CiEvidenceAggregator.ValidateForTesting(fixture.CreateOptions(), mutation));
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

    private sealed class CiEvidenceFixture : IDisposable
    {
        private static readonly string[] Platforms = ["Windows", "Linux", "macOS"];
        private static readonly string[] LifecyclePhases =
        [
            "load",
            "assembly-info",
            "discovery",
            "execution",
            "assembly-teardown",
            "process-exit"
        ];
        private static readonly (string Assembly, string Phase)[] LifecycleGateResults =
        [
            ("Gate.Forensics", "forensics-self-test"),
            ("Gate.ProcessLease", "owned-tree-self-test"),
            ("Gate.MarkerReader", "marker-reader-self-test")
        ];
        private readonly Dictionary<string, string> _upstreamResults = new(StringComparer.Ordinal)
        {
            ["release-build"] = "success",
            ["debug-build"] = "success",
            ["repository-suite"] = "success",
            ["review-mutations"] = "success",
            ["assembly-lifecycle"] = "success",
            ["format"] = "success",
            ["aria2-tls-security"] = "success",
            ["package-audit"] = "success"
        };

        private CiEvidenceFixture(string root, int proofCount)
        {
            Root = root;
            ProofCount = proofCount;
        }

        internal string Root { get; }

        internal int ProofCount { get; }

        internal static CiEvidenceFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"downkyi-ci-evidence-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var proofs = ReadProofs();
            var fixture = new CiEvidenceFixture(root, proofs.Count);
            fixture.WriteBuildEvidence();
            fixture.WriteRepositoryEvidence();
            fixture.WriteReviewEvidence(proofs);
            fixture.WriteLifecycleEvidence();
            return fixture;
        }

        internal CiEvidenceAggregationOptions CreateOptions() =>
            new(RepositoryRoot, Root, ExpectedSha, "PR", _upstreamResults);

        internal void ApplyScenario(string scenario)
        {
            var evidenceFiles = Directory.GetFiles(Root, "evidence.json", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .ToArray();
            switch (scenario)
            {
                case "delete-shard":
                case "aggregator-false-green":
                    File.Delete(Directory.GetFiles(
                        Path.Combine(Root, "repository"),
                        "evidence.json",
                        SearchOption.AllDirectories).Order(StringComparer.Ordinal).First());
                    break;
                case "duplicate-shard":
                    var source = Directory.GetFiles(
                        Path.Combine(Root, "repository"),
                        "evidence.json",
                        SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
                    var duplicateDirectory = Path.Combine(Root, "repository", "duplicate");
                    Directory.CreateDirectory(duplicateDirectory);
                    File.Copy(source, Path.Combine(duplicateDirectory, "evidence.json"));
                    break;
                case "wrong-sha":
                    ReplaceJsonValue(evidenceFiles[0], "commitSha", new string('b', 40));
                    break;
                case "malformed-evidence":
                    File.WriteAllText(evidenceFiles[0], "{");
                    break;
                case "zero-executed":
                    var repositoryEvidence = Directory.GetFiles(
                        Path.Combine(Root, "repository"),
                        "evidence.json",
                        SearchOption.AllDirectories).Order(StringComparer.Ordinal).First();
                    var repositoryJson = JsonNode.Parse(File.ReadAllText(repositoryEvidence))!.AsObject();
                    repositoryJson["projects"]!.AsArray()[0]!["executed"] = 0;
                    File.WriteAllText(repositoryEvidence, repositoryJson.ToJsonString());
                    break;
                case "skipped-required-shard":
                    _upstreamResults["repository-suite"] = "cancelled";
                    break;
                case "unexpected-cardinality":
                    var reviewEvidence = Directory.GetFiles(
                            Path.Combine(Root, "review"),
                            "evidence.json",
                            SearchOption.AllDirectories)
                        .Select(path => (Path: path, Json: JsonNode.Parse(File.ReadAllText(path))!.AsObject()))
                        .First(item => item.Json["proofs"]!.AsArray().Any(proof =>
                            proof?["expectedFailedTests"]?.GetValue<int>() == 1));
                    var proof = reviewEvidence.Json["proofs"]!.AsArray().First(item =>
                        item?["expectedFailedTests"]?.GetValue<int>() == 1)!;
                    proof["failed"] = 2;
                    File.WriteAllText(reviewEvidence.Path, reviewEvidence.Json.ToJsonString());
                    break;
                case "missing-lifecycle-iteration":
                    var lifecycleEvidence = Directory.GetFiles(
                            Path.Combine(Root, "lifecycle"),
                            "assembly-lifecycle-report.json",
                            SearchOption.AllDirectories)
                        .Order(StringComparer.Ordinal)
                        .First();
                    var lifecycleJson = JsonNode.Parse(File.ReadAllText(lifecycleEvidence))!.AsObject();
                    var lifecycleResults = lifecycleJson["results"]!.AsArray();
                    var requiredPhase = lifecycleResults.First(result =>
                        result?["assembly"]?.GetValue<string>()?.StartsWith(
                            "DownKyi.",
                            StringComparison.Ordinal) == true);
                    lifecycleResults.Remove(requiredPhase);
                    File.WriteAllText(lifecycleEvidence, lifecycleJson.ToJsonString());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
        }

        internal void RemoveLifecycleGateEvidence()
        {
            var authorityPath = FindLifecycleGateAuthorityReport();
            var json = JsonNode.Parse(File.ReadAllText(authorityPath))!.AsObject();
            var results = json["results"]!.AsArray();
            var gate = results.First(result =>
                result?["assembly"]?.GetValue<string>() == "Gate.Forensics");
            results.Remove(gate);
            File.WriteAllText(authorityPath, json.ToJsonString());
        }

        internal void CopyLifecycleGateEvidenceToNonAuthority()
        {
            var authorityPath = FindLifecycleGateAuthorityReport();
            var authority = JsonNode.Parse(File.ReadAllText(authorityPath))!.AsObject();
            var gate = authority["results"]!.AsArray().First(result =>
                result?["assembly"]?.GetValue<string>() == "Gate.Forensics")!;
            var nonAuthorityPath = Directory.GetFiles(
                    Path.Combine(Root, "lifecycle"),
                    "assembly-lifecycle-report.json",
                    SearchOption.AllDirectories)
                .First(path => !string.Equals(path, authorityPath, StringComparison.Ordinal));
            var nonAuthority = JsonNode.Parse(File.ReadAllText(nonAuthorityPath))!.AsObject();
            nonAuthority["results"]!.AsArray().Add(gate.DeepClone());
            File.WriteAllText(nonAuthorityPath, nonAuthority.ToJsonString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static void ReplaceJsonValue(string path, string property, string value)
        {
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json[property] = value;
            File.WriteAllText(path, json.ToJsonString());
        }

        private void WriteBuildEvidence()
        {
            var builds = new[]
            {
                ("Windows", "Release"),
                ("Linux", "Release"),
                ("macOS", "Release"),
                ("Windows", "Debug")
            };
            foreach (var (platform, configuration) in builds)
            {
                var directory = Path.Combine(Root, "build", platform, configuration);
                WriteJson(Path.Combine(directory, "evidence.json"), new
                {
                    schemaVersion = 1,
                    kind = "build",
                    identity = $"build/{platform}/{configuration}",
                    commitSha = ExpectedSha,
                    platform,
                    configuration,
                    successful = true
                });
            }
        }

        private void WriteRepositoryEvidence()
        {
            var expectedClasses = ReadNormalClasses();
            var allProjects = Directory.GetFiles(
                Path.Combine(RepositoryRoot, "tests"),
                "*.Tests.csproj",
                SearchOption.AllDirectories);
            foreach (var platform in Platforms)
            {
                var projects = CentralTestPolicy.SelectProjects(allProjects, platform)
                    .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
                    .ToArray();
                for (var shardIndex = 0; shardIndex < 2; shardIndex++)
                {
                    var directory = Path.Combine(Root, "repository", platform, $"shard-{shardIndex}");
                    var projectEvidence = new List<object>();
                    foreach (var project in projects.Where((_, index) => index % 2 == shardIndex))
                    {
                        var projectName = Path.GetFileNameWithoutExtension(project);
                        var trxName = $"{projectName}.trx";
                        var classes = expectedClasses.TryGetValue(project, out var required)
                            ? required
                            : new[] { $"{projectName}.Smoke" };
                        WriteTrx(Path.Combine(directory, trxName), classes, passed: true, repeat: 1);
                        projectEvidence.Add(new
                        {
                            project,
                            trxFile = trxName,
                            executed = classes.Count,
                            failed = 0,
                            exitCode = 0,
                            ownershipEstablished = true
                        });
                    }
                    WriteJson(Path.Combine(directory, "evidence.json"), new
                    {
                        schemaVersion = 1,
                        kind = "repository-suite",
                        identity = $"repository/{platform}/{shardIndex}-of-2",
                        commitSha = ExpectedSha,
                        platform,
                        shardIndex,
                        shardCount = 2,
                        projects = projectEvidence,
                        successful = true
                    });
                }
            }
        }

        private void WriteReviewEvidence(IReadOnlyList<ProofContract> proofs)
        {
            var ordered = proofs.OrderBy(item => item.ProofId, StringComparer.Ordinal).ToArray();
            foreach (var platform in Platforms)
            {
                for (var shardIndex = 0; shardIndex < 4; shardIndex++)
                {
                    var directory = Path.Combine(Root, "review", platform, $"shard-{shardIndex}");
                    var proofEvidence = new List<object>();
                    foreach (var proof in ordered.Where((_, index) => index % 4 == shardIndex))
                    {
                        var safeId = string.Concat(proof.ProofId.Select(character =>
                            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                                ? character
                                : '-'));
                        var trxFile = $"proofs/{safeId}/result.trx";
                        var failed = proof.ExpectedFailedTests ?? 1;
                        WriteTrx(
                            Path.Combine(directory, trxFile),
                            new[] { proof.Class },
                            passed: false,
                            repeat: failed);
                        proofEvidence.Add(new
                        {
                            proofId = proof.ProofId,
                            invariantId = proof.InvariantId,
                            project = proof.Project,
                            @class = proof.Class,
                            environmentVariable = proof.EnvironmentVariable,
                            environmentValue = proof.EnvironmentValue,
                            trxFile,
                            runnerExitCode = 1,
                            executed = failed,
                            failed,
                            expectedFailedTests = proof.ExpectedFailedTests
                        });
                    }
                    WriteJson(Path.Combine(directory, "evidence.json"), new
                    {
                        schemaVersion = 1,
                        kind = "review-mutations",
                        identity = $"review/{platform}/{shardIndex}-of-4",
                        commitSha = ExpectedSha,
                        platform,
                        shardIndex,
                        shardCount = 4,
                        proofs = proofEvidence,
                        successful = true
                    });
                }
            }
        }

        private void WriteLifecycleEvidence()
        {
            using var topology = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "docs",
                "testing",
                "ci-required-topology.json")));
            var gateAuthorityAssembly = topology.RootElement.GetProperty("lifecycle")
                .GetProperty("gateAuthorityAssembly")
                .GetString();
            var allProjects = Directory.GetFiles(
                Path.Combine(RepositoryRoot, "tests"),
                "*.Tests.csproj",
                SearchOption.AllDirectories);
            var assemblies = CentralTestPolicy.SelectProjects(allProjects, "Windows")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToArray();
            foreach (var assembly in assemblies)
            {
                var results = new List<object>();
                if (string.Equals(assembly, gateAuthorityAssembly, StringComparison.Ordinal))
                {
                    results.AddRange(LifecycleGateResults.Select(gate => (object)new
                    {
                        assembly = gate.Assembly,
                        iteration = 1,
                        phase = gate.Phase,
                        success = true
                    }));
                }
                for (var iteration = 1; iteration <= 3; iteration++)
                {
                    results.AddRange(LifecyclePhases.Select(phase => new
                    {
                        assembly,
                        iteration,
                        phase,
                        success = true
                    }));
                }
                WriteJson(
                    Path.Combine(Root, "lifecycle", assembly!, "assembly-lifecycle-report.json"),
                    new
                    {
                        schemaVersion = 4,
                        profile = "PR",
                        iterations = 3,
                        commitSha = ExpectedSha,
                        workingTreeDirty = false,
                        testAssemblyCount = 1,
                        successful = true,
                        ownershipAuditPassed = true,
                        failedPhaseCount = 0,
                        results
                    });
            }
        }

        private string FindLifecycleGateAuthorityReport() =>
            Directory.GetFiles(
                    Path.Combine(Root, "lifecycle"),
                    "assembly-lifecycle-report.json",
                    SearchOption.AllDirectories)
                .Single(path =>
                {
                    var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                    return json["results"]!.AsArray().Any(result =>
                        result?["assembly"]?.GetValue<string>() == "Gate.Forensics");
                });

        private static Dictionary<string, IReadOnlyList<string>> ReadNormalClasses()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(RepositoryRoot, "docs", "testing", "review-invariant-corpus.json")));
            return document.RootElement.GetProperty("prInvariants")
                .EnumerateArray()
                .SelectMany(invariant => invariant.GetProperty("testClasses").EnumerateArray())
                .GroupBy(
                    item => item.GetProperty("project").GetString()!,
                    item => item.GetProperty("class").GetString()!,
                    StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
        }

        private static List<ProofContract> ReadProofs()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(RepositoryRoot, "docs", "testing", "review-invariant-corpus.json")));
            var proofs = new List<ProofContract>();
            foreach (var invariant in document.RootElement.GetProperty("prInvariants").EnumerateArray())
            {
                if (!invariant.TryGetProperty("adversarialProofs", out var adversarialProofs))
                {
                    continue;
                }
                var invariantId = invariant.GetProperty("id").GetString()!;
                foreach (var proof in adversarialProofs.EnumerateArray())
                {
                    var environment = proof.GetProperty("environmentVariable").GetString()!;
                    proofs.Add(new ProofContract(
                        $"{invariantId}/{environment}",
                        invariantId,
                        proof.GetProperty("project").GetString()!,
                        proof.GetProperty("class").GetString()!,
                        environment,
                        proof.GetProperty("environmentValue").GetString()!,
                        proof.TryGetProperty("expectedFailedTests", out var expected)
                            ? expected.GetInt32()
                            : null));
                }
            }
            return proofs;
        }

        private static void WriteJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        private static void WriteTrx(
            string path,
            IReadOnlyList<string> classes,
            bool passed,
            int repeat)
        {
            var results = new List<XElement>();
            var definitions = new List<XElement>();
            var identifier = 0;
            foreach (var className in classes)
            {
                for (var index = 0; index < repeat; index++)
                {
                    identifier++;
                    var id = $"test-{identifier}";
                    results.Add(new XElement("UnitTestResult",
                        new XAttribute("testId", id),
                        new XAttribute("executionId", $"execution-{identifier}"),
                        new XAttribute("testName", $"Probe{identifier}"),
                        new XAttribute("outcome", passed ? "Passed" : "Failed")));
                    definitions.Add(new XElement("UnitTest",
                        new XAttribute("id", id),
                        new XAttribute("name", $"Probe{identifier}"),
                        new XElement("TestMethod",
                            new XAttribute("className", className),
                            new XAttribute("name", $"Probe{identifier}"))));
                }
            }
            var total = results.Count;
            var document = new XDocument(
                new XElement("TestRun",
                    new XElement("Results", results),
                    new XElement("TestDefinitions", definitions),
                    new XElement("ResultSummary",
                        new XAttribute("outcome", "Completed"),
                        new XElement("Counters",
                            new XAttribute("total", total),
                            new XAttribute("executed", total),
                            new XAttribute("passed", passed ? total : 0),
                            new XAttribute("failed", passed ? 0 : total)))));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            document.Save(path);
        }

        private sealed record ProofContract(
            string ProofId,
            string InvariantId,
            string Project,
            string Class,
            string EnvironmentVariable,
            string EnvironmentValue,
            int? ExpectedFailedTests);
    }
}
