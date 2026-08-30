using System.Collections.ObjectModel;
using System.Text.Json;

#pragma warning disable CA1515 // PowerShell invokes this compiled fail-closed evidence owner.

namespace DownKyi.CentralTestRunner;

public sealed class CiEvidenceAggregationOptions
{
    public CiEvidenceAggregationOptions(
        string repositoryRoot,
        string evidenceRoot,
        string expectedCommitSha,
        string expectedLifecycleProfile,
        IReadOnlyDictionary<string, string> upstreamResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommitSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLifecycleProfile);
        ArgumentNullException.ThrowIfNull(upstreamResults);
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        EvidenceRoot = Path.GetFullPath(evidenceRoot);
        ExpectedCommitSha = expectedCommitSha;
        ExpectedLifecycleProfile = expectedLifecycleProfile;
        UpstreamResults = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(upstreamResults, StringComparer.Ordinal));
    }

    public string RepositoryRoot { get; }

    public string EvidenceRoot { get; }

    public string ExpectedCommitSha { get; }

    public string ExpectedLifecycleProfile { get; }

    public IReadOnlyDictionary<string, string> UpstreamResults { get; }
}

public sealed record CiEvidenceAggregationResult(
    int BuildCount,
    int RepositoryShardCount,
    int RepositoryProjectCount,
    int ReviewShardCount,
    int ReviewProofCount,
    int LifecycleAssemblyCount);

public static class CiEvidenceAggregator
{
    private const string TopologyPath = "docs/testing/ci-required-topology.json";
    private const string CorpusPath = "docs/testing/review-invariant-corpus.json";
    private static readonly string[] RequiredPlatforms = ["Windows", "Linux", "macOS"];

    public static CiEvidenceAggregationResult Validate(CiEvidenceAggregationOptions options) =>
        ValidateCore(options);

    internal static CiEvidenceAggregationResult ValidateForTesting(
        CiEvidenceAggregationOptions options,
        CiEvidenceAggregatorMutation mutation)
    {
        if (mutation == CiEvidenceAggregatorMutation.ReturnFalseGreen)
        {
            return new CiEvidenceAggregationResult(4, 6, 24, 12, 123, 8);
        }
        return ValidateCore(options);
    }

    private static CiEvidenceAggregationResult ValidateCore(CiEvidenceAggregationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(options.EvidenceRoot))
        {
            throw new DirectoryNotFoundException("The downloaded CI evidence root is missing.");
        }

        using var topologyDocument = ReadJson(Path.Combine(options.RepositoryRoot, TopologyPath));
        var topology = topologyDocument.RootElement;
        RequireSchema(topology, 1, "CI topology");
        ValidateUpstreamResults(topology, options.UpstreamResults);
        var lanes = ReadLanes(topology);
        var expectedBuilds = ReadExpectedBuilds(topology);
        var repositoryShardCount = ReadPositiveInt(topology, "repositoryShardCount");
        var reviewShardCount = ReadPositiveInt(topology, "reviewMutationShardCount");

        var evidenceFiles = Directory.GetFiles(
            options.EvidenceRoot,
            "evidence.json",
            SearchOption.AllDirectories);
        if (evidenceFiles.Length == 0)
        {
            throw new InvalidDataException("No structured CI evidence files were downloaded.");
        }

        var builds = new List<BuildEvidence>();
        var repositoryShards = new List<RepositoryShardEvidence>();
        var reviewShards = new List<ReviewShardEvidence>();
        foreach (var evidenceFile in evidenceFiles)
        {
            using var document = ReadJson(evidenceFile);
            var root = document.RootElement;
            RequireSchema(root, 1, "CI evidence");
            ValidateCommit(root, options.ExpectedCommitSha);
            if (!ReadRequiredBoolean(root, "successful"))
            {
                throw new InvalidDataException("CI evidence cannot declare an unsuccessful shard.");
            }

            switch (ReadRequiredString(root, "kind"))
            {
                case "build":
                    builds.Add(ReadBuild(root, evidenceFile));
                    break;
                case "repository-suite":
                    repositoryShards.Add(ReadRepositoryShard(root, evidenceFile));
                    break;
                case "review-mutations":
                    reviewShards.Add(ReadReviewShard(root, evidenceFile));
                    break;
                default:
                    throw new InvalidDataException("CI evidence has an unknown kind.");
            }
        }

        ValidateUniqueIdentities(builds.Select(item => item.Identity));
        ValidateUniqueIdentities(repositoryShards.Select(item => item.Identity));
        ValidateUniqueIdentities(reviewShards.Select(item => item.Identity));
        ValidateBuilds(builds, expectedBuilds);
        var repositoryProjects = ValidateRepositoryShards(
            options,
            lanes,
            repositoryShardCount,
            repositoryShards);
        var reviewProofCount = ValidateReviewShards(
            options,
            lanes,
            reviewShardCount,
            reviewShards,
            repositoryProjects);
        var lifecycleCount = ValidateLifecycleEvidence(options, topology, lanes);

        return new CiEvidenceAggregationResult(
            builds.Count,
            repositoryShards.Count,
            repositoryProjects.Count,
            reviewShards.Count,
            reviewProofCount,
            lifecycleCount);
    }

    private static void ValidateUpstreamResults(
        JsonElement topology,
        IReadOnlyDictionary<string, string> upstreamResults)
    {
        var expected = topology.GetProperty("requiredUpstreamJobs")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        if (expected.Length == 0 || expected.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("The required upstream job set is empty or malformed.");
        }
        if (!upstreamResults.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                expected.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("The aggregator did not receive every exact upstream job result.");
        }
        foreach (var job in expected)
        {
            if (!string.Equals(upstreamResults[job], "success", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Required upstream job '{job}' did not succeed.");
            }
        }
    }

    private static ReadOnlyCollection<CiLane> ReadLanes(JsonElement topology)
    {
        var lanes = topology.GetProperty("lanes")
            .EnumerateArray()
            .Select(item => new CiLane(
                ReadRequiredString(item, "platform"),
                ReadRequiredString(item, "runner")))
            .ToArray();
        if (lanes.Length != 3 ||
            !lanes.Select(item => item.Platform).SequenceEqual(
                RequiredPlatforms,
                StringComparer.Ordinal) ||
            lanes.Select(item => item.Platform).Distinct(StringComparer.Ordinal).Count() != lanes.Length)
        {
            throw new InvalidDataException("The required CI lane set must contain Windows, Linux and macOS once.");
        }
        return new ReadOnlyCollection<CiLane>(lanes);
    }

    private static HashSet<string> ReadExpectedBuilds(JsonElement topology)
    {
        var builds = topology.GetProperty("builds")
            .EnumerateArray()
            .Select(item => $"build/{ReadRequiredString(item, "platform")}/{ReadRequiredString(item, "configuration")}")
            .ToHashSet(StringComparer.Ordinal);
        if (builds.Count != 4)
        {
            throw new InvalidDataException("The build evidence contract must contain three Release lanes and Windows Debug.");
        }
        return builds;
    }

    private static void ValidateBuilds(
        IReadOnlyCollection<BuildEvidence> builds,
        IReadOnlySet<string> expectedBuilds)
    {
        var actual = builds.Select(item => item.Identity).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expectedBuilds))
        {
            throw new InvalidDataException("Build evidence is missing, duplicated or unexpected.");
        }
    }

    private static Dictionary<string, RepositoryProjectEvidence> ValidateRepositoryShards(
        CiEvidenceAggregationOptions options,
        IReadOnlyList<CiLane> lanes,
        int shardCount,
        IReadOnlyCollection<RepositoryShardEvidence> shards)
    {
        var expectedShardTotal = lanes.Count * shardCount;
        if (shards.Count != expectedShardTotal)
        {
            throw new InvalidDataException("Repository-suite evidence has a missing or duplicate shard.");
        }

        var allProjects = Directory.GetFiles(
            Path.Combine(options.RepositoryRoot, "tests"),
            "*.Tests.csproj",
            SearchOption.AllDirectories);
        var projectEvidence = new Dictionary<string, RepositoryProjectEvidence>(StringComparer.Ordinal);
        foreach (var lane in lanes)
        {
            var expectedProjects = CentralTestPolicy.SelectProjects(allProjects, lane.Platform)
                .Select(path => FormatRepositoryPath(options.RepositoryRoot, path))
                .ToArray();
            for (var shardIndex = 0; shardIndex < shardCount; shardIndex++)
            {
                var matches = shards.Where(item =>
                    string.Equals(item.Platform, lane.Platform, StringComparison.Ordinal) &&
                    item.ShardIndex == shardIndex &&
                    item.ShardCount == shardCount).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidDataException("Repository-suite evidence has a missing or duplicate lane shard.");
                }
                var expectedShardProjects = expectedProjects
                    .Where((_, index) => index % shardCount == shardIndex)
                    .ToArray();
                if (!matches[0].Projects.Select(item => item.Project).SequenceEqual(
                        expectedShardProjects,
                        StringComparer.Ordinal))
                {
                    throw new InvalidDataException("Repository-suite shard project membership is incomplete or duplicated.");
                }
                foreach (var project in matches[0].Projects)
                {
                    ValidateRepositoryProject(project);
                    var key = $"{lane.Platform}|{project.Project}";
                    if (!projectEvidence.TryAdd(key, project))
                    {
                        throw new InvalidDataException("Repository project evidence was duplicated.");
                    }
                }
            }
        }
        return projectEvidence;
    }

    private static void ValidateRepositoryProject(RepositoryProjectEvidence project)
    {
        if (project.ExitCode != 0 || project.Executed < 1 || project.Failed != 0 ||
            !project.OwnershipEstablished)
        {
            throw new InvalidDataException("Repository project evidence is not a successful owned execution.");
        }
        var report = CentralTestExecutionValidator.ValidateReport(project.TrxPath);
        if (report.Executed != project.Executed || report.Failed != project.Failed)
        {
            throw new InvalidDataException("Repository project TRX counters do not match its evidence envelope.");
        }
    }

    private static int ValidateReviewShards(
        CiEvidenceAggregationOptions options,
        IReadOnlyList<CiLane> lanes,
        int shardCount,
        IReadOnlyCollection<ReviewShardEvidence> shards,
        IReadOnlyDictionary<string, RepositoryProjectEvidence> repositoryProjects)
    {
        var expectedProofs = ReadExpectedProofs(options.RepositoryRoot);
        if (shards.Count != lanes.Count * shardCount)
        {
            throw new InvalidDataException("Review evidence has a missing or duplicate shard.");
        }

        var proofCount = 0;
        foreach (var lane in lanes)
        {
            ValidateNormalInvariantReuse(options.RepositoryRoot, lane.Platform, repositoryProjects);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var shardIndex = 0; shardIndex < shardCount; shardIndex++)
            {
                var matches = shards.Where(item =>
                    string.Equals(item.Platform, lane.Platform, StringComparison.Ordinal) &&
                    item.ShardIndex == shardIndex &&
                    item.ShardCount == shardCount).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidDataException("Review evidence has a missing or duplicate lane shard.");
                }
                var expectedIds = expectedProofs.Values
                    .OrderBy(item => item.ProofId, StringComparer.Ordinal)
                    .Where((_, index) => index % shardCount == shardIndex)
                    .Select(item => item.ProofId)
                    .ToArray();
                if (!matches[0].Proofs.Select(item => item.ProofId).SequenceEqual(
                        expectedIds,
                        StringComparer.Ordinal))
                {
                    throw new InvalidDataException("Review mutation shard membership is incomplete or duplicated.");
                }
                foreach (var proof in matches[0].Proofs)
                {
                    if (!seen.Add(proof.ProofId) || !expectedProofs.TryGetValue(proof.ProofId, out var expected))
                    {
                        throw new InvalidDataException("Review proof evidence is duplicate or unexpected.");
                    }
                    ValidateReviewProof(proof, expected);
                    proofCount++;
                }
            }
            if (!seen.SetEquals(expectedProofs.Keys))
            {
                throw new InvalidDataException("Review proof evidence is incomplete.");
            }
        }
        return proofCount;
    }

    private static void ValidateNormalInvariantReuse(
        string repositoryRoot,
        string platform,
        IReadOnlyDictionary<string, RepositoryProjectEvidence> repositoryProjects)
    {
        using var corpus = ReadJson(Path.Combine(repositoryRoot, CorpusPath));
        var classes = corpus.RootElement.GetProperty("prInvariants")
            .EnumerateArray()
            .SelectMany(invariant => invariant.GetProperty("testClasses").EnumerateArray())
            .GroupBy(item => ReadRequiredString(item, "project"), StringComparer.Ordinal);
        foreach (var projectClasses in classes)
        {
            var key = $"{platform}|{projectClasses.Key}";
            if (!repositoryProjects.TryGetValue(key, out var project))
            {
                throw new InvalidDataException("Normal review invariant evidence has no equivalent repository project execution.");
            }
            var expectedClasses = projectClasses
                .Select(item => ReadRequiredString(item, "class"))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var report = CentralTestExecutionValidator.ValidateReport(project.TrxPath, expectedClasses);
            if (report.Failed != 0 || report.PassedExpectedClasses != expectedClasses.Length)
            {
                throw new InvalidDataException("Normal review invariant evidence was not fully passed by the equivalent repository TRX.");
            }
        }
    }

    private static Dictionary<string, ExpectedProof> ReadExpectedProofs(string repositoryRoot)
    {
        using var corpus = ReadJson(Path.Combine(repositoryRoot, CorpusPath));
        RequireSchema(corpus.RootElement, 1, "review invariant corpus");
        var proofs = new Dictionary<string, ExpectedProof>(StringComparer.Ordinal);
        foreach (var invariant in corpus.RootElement.GetProperty("prInvariants").EnumerateArray())
        {
            var invariantId = ReadRequiredString(invariant, "id");
            if (!invariant.TryGetProperty("adversarialProofs", out var adversarialProofs))
            {
                continue;
            }
            foreach (var proof in adversarialProofs.EnumerateArray())
            {
                var environmentVariable = ReadRequiredString(proof, "environmentVariable");
                var proofId = $"{invariantId}/{environmentVariable}";
                int? expectedFailed = proof.TryGetProperty("expectedFailedTests", out var expectedFailedElement)
                    ? expectedFailedElement.GetInt32()
                    : null;
                if (!proofs.TryAdd(proofId, new ExpectedProof(
                        proofId,
                        invariantId,
                        ReadRequiredString(proof, "project"),
                        ReadRequiredString(proof, "class"),
                        environmentVariable,
                        ReadRequiredString(proof, "environmentValue"),
                        expectedFailed)))
                {
                    throw new InvalidDataException("The review corpus contains duplicate proof identities.");
                }
            }
        }
        if (proofs.Count == 0)
        {
            throw new InvalidDataException("The review corpus contains no adversarial proofs.");
        }
        return proofs;
    }

    private static void ValidateReviewProof(ReviewProofEvidence proof, ExpectedProof expected)
    {
        if (!string.Equals(proof.InvariantId, expected.InvariantId, StringComparison.Ordinal) ||
            !string.Equals(proof.Project, expected.Project, StringComparison.Ordinal) ||
            !string.Equals(proof.Class, expected.Class, StringComparison.Ordinal) ||
            !string.Equals(proof.EnvironmentVariable, expected.EnvironmentVariable, StringComparison.Ordinal) ||
            !string.Equals(proof.EnvironmentValue, expected.EnvironmentValue, StringComparison.Ordinal) ||
            proof.RunnerExitCode == 0 || proof.Executed < 1 || proof.Failed < 1 ||
            proof.ExpectedFailedTests != expected.ExpectedFailedTests ||
            (expected.ExpectedFailedTests.HasValue && proof.Failed != expected.ExpectedFailedTests.Value))
        {
            throw new InvalidDataException("Review proof evidence does not match its fail-closed corpus contract.");
        }
        var report = CentralTestExecutionValidator.ValidateReport(proof.TrxPath, new[] { expected.Class });
        if (report.Executed != proof.Executed || report.Failed != proof.Failed)
        {
            throw new InvalidDataException("Review mutation TRX counters do not match its evidence envelope.");
        }
    }

    private static int ValidateLifecycleEvidence(
        CiEvidenceAggregationOptions options,
        JsonElement topology,
        IReadOnlyList<CiLane> lanes)
    {
        var lifecycle = topology.GetProperty("lifecycle");
        var platform = ReadRequiredString(lifecycle, "platform");
        if (!lanes.Any(lane => string.Equals(lane.Platform, platform, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Lifecycle platform is not a required CI lane.");
        }
        var profiles = lifecycle.GetProperty("profiles");
        if (!profiles.TryGetProperty(options.ExpectedLifecycleProfile, out var iterationElement))
        {
            throw new InvalidDataException("The requested lifecycle profile is not part of the required topology.");
        }
        var profile = options.ExpectedLifecycleProfile;
        var iterations = iterationElement.GetInt32();
        if (iterations < 1)
        {
            throw new InvalidDataException("The requested lifecycle profile has no required iterations.");
        }
        var phases = lifecycle.GetProperty("phases").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        var expectedGateResults = lifecycle.GetProperty("gateResults")
            .EnumerateArray()
            .Select(item => $"{ReadRequiredString(item, "assembly")}|{ReadRequiredString(item, "phase")}")
            .ToHashSet(StringComparer.Ordinal);
        if (phases.Length == 0 || phases.Any(string.IsNullOrWhiteSpace) ||
            phases.Distinct(StringComparer.Ordinal).Count() != phases.Length ||
            expectedGateResults.Count == 0)
        {
            throw new InvalidDataException("Lifecycle phase or gate-result topology is malformed.");
        }
        var allProjects = Directory.GetFiles(
            Path.Combine(options.RepositoryRoot, "tests"),
            "*.Tests.csproj",
            SearchOption.AllDirectories);
        var expectedAssemblies = CentralTestPolicy.SelectProjects(allProjects, platform)
            .Select(path => Path.GetFileNameWithoutExtension(path) ??
                            throw new InvalidDataException("A lifecycle test project has no assembly name."))
            .ToHashSet(StringComparer.Ordinal);
        var reportPaths = Directory.GetFiles(
            options.EvidenceRoot,
            "assembly-lifecycle-report.json",
            SearchOption.AllDirectories);
        if (reportPaths.Length != expectedAssemblies.Count)
        {
            throw new InvalidDataException("Lifecycle evidence has a missing or duplicate assembly report.");
        }
        var seenAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reportPath in reportPaths)
        {
            using var document = ReadJson(reportPath);
            var report = document.RootElement;
            RequireSchema(report, 4, "lifecycle evidence");
            if (!string.Equals(ReadRequiredString(report, "commitSha"), options.ExpectedCommitSha, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadRequiredString(report, "profile"), profile, StringComparison.Ordinal) ||
                report.GetProperty("iterations").GetInt32() != iterations ||
                report.GetProperty("testAssemblyCount").GetInt32() != 1 ||
                ReadRequiredBoolean(report, "workingTreeDirty") ||
                !ReadRequiredBoolean(report, "successful") ||
                !ReadRequiredBoolean(report, "ownershipAuditPassed") ||
                report.GetProperty("failedPhaseCount").GetInt32() != 0)
            {
                throw new InvalidDataException("Lifecycle report does not satisfy exact-head PR semantics.");
            }
            var results = report.GetProperty("results").EnumerateArray().ToArray();
            var assemblies = results
                .Select(item => ReadRequiredString(item, "assembly"))
                .Where(expectedAssemblies.Contains)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (assemblies.Length != 1 || !expectedAssemblies.Contains(assemblies[0]) ||
                !seenAssemblies.Add(assemblies[0]))
            {
                throw new InvalidDataException("Lifecycle report assembly identity is missing, duplicate or unexpected.");
            }
            var observed = new HashSet<string>(StringComparer.Ordinal);
            var observedGateResults = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                var assembly = ReadRequiredString(result, "assembly");
                var iteration = result.GetProperty("iteration").GetInt32();
                var phase = ReadRequiredString(result, "phase");
                if (!string.Equals(assembly, assemblies[0], StringComparison.Ordinal))
                {
                    var gateIdentity = $"{assembly}|{phase}";
                    if (iteration != 1 || !ReadRequiredBoolean(result, "success") ||
                        !expectedGateResults.Contains(gateIdentity) ||
                        !observedGateResults.Add(gateIdentity))
                    {
                        throw new InvalidDataException(
                            "Lifecycle gate evidence is failed, duplicated or unexpected.");
                    }
                    continue;
                }
                if (iteration < 1 || iteration > iterations || !phases.Contains(phase, StringComparer.Ordinal) ||
                    !ReadRequiredBoolean(result, "success") || !observed.Add($"{iteration}|{phase}"))
                {
                    throw new InvalidDataException("Lifecycle phase evidence is failed, duplicated or unexpected.");
                }
            }
            var expectedPhaseCount = iterations * phases.Length;
            if (observed.Count != expectedPhaseCount)
            {
                throw new InvalidDataException("Lifecycle evidence has a missing iteration or phase.");
            }
            if (!observedGateResults.SetEquals(expectedGateResults))
            {
                throw new InvalidDataException("Lifecycle evidence has a missing or duplicate gate self-test.");
            }
        }
        if (!seenAssemblies.SetEquals(expectedAssemblies))
        {
            throw new InvalidDataException("Lifecycle evidence does not contain every expected assembly exactly once.");
        }
        return seenAssemblies.Count;
    }

    private static BuildEvidence ReadBuild(JsonElement root, string path)
    {
        var platform = ReadRequiredString(root, "platform");
        var configuration = ReadRequiredString(root, "configuration");
        var identity = ReadRequiredString(root, "identity");
        if (!string.Equals(identity, $"build/{platform}/{configuration}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Build evidence identity is inconsistent.");
        }
        return new BuildEvidence(identity, path);
    }

    private static RepositoryShardEvidence ReadRepositoryShard(JsonElement root, string evidencePath)
    {
        var evidenceDirectory = Path.GetDirectoryName(evidencePath)!;
        var platform = ReadRequiredString(root, "platform");
        var shardIndex = root.GetProperty("shardIndex").GetInt32();
        var shardCount = root.GetProperty("shardCount").GetInt32();
        var identity = ReadRequiredString(root, "identity");
        if (!string.Equals(
                identity,
                $"repository/{platform}/{shardIndex}-of-{shardCount}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Repository shard evidence identity is inconsistent.");
        }
        var projects = root.GetProperty("projects").EnumerateArray().Select(item =>
        {
            var trxPath = ResolveEvidencePath(
                evidenceDirectory,
                ReadRequiredString(item, "trxFile"));
            return new RepositoryProjectEvidence(
                ReadRequiredString(item, "project"),
                trxPath,
                item.GetProperty("executed").GetInt32(),
                item.GetProperty("failed").GetInt32(),
                item.GetProperty("exitCode").GetInt32(),
                ReadRequiredBoolean(item, "ownershipEstablished"));
        }).ToArray();
        return new RepositoryShardEvidence(
            identity,
            platform,
            shardIndex,
            shardCount,
            new ReadOnlyCollection<RepositoryProjectEvidence>(projects));
    }

    private static ReviewShardEvidence ReadReviewShard(JsonElement root, string evidencePath)
    {
        var evidenceDirectory = Path.GetDirectoryName(evidencePath)!;
        var platform = ReadRequiredString(root, "platform");
        var shardIndex = root.GetProperty("shardIndex").GetInt32();
        var shardCount = root.GetProperty("shardCount").GetInt32();
        var identity = ReadRequiredString(root, "identity");
        if (!string.Equals(
                identity,
                $"review/{platform}/{shardIndex}-of-{shardCount}",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Review shard evidence identity is inconsistent.");
        }
        var proofs = root.GetProperty("proofs").EnumerateArray().Select(item =>
        {
            int? expectedFailed = item.TryGetProperty("expectedFailedTests", out var expectedElement) &&
                                  expectedElement.ValueKind != JsonValueKind.Null
                ? expectedElement.GetInt32()
                : null;
            return new ReviewProofEvidence(
                ReadRequiredString(item, "proofId"),
                ReadRequiredString(item, "invariantId"),
                ReadRequiredString(item, "project"),
                ReadRequiredString(item, "class"),
                ReadRequiredString(item, "environmentVariable"),
                ReadRequiredString(item, "environmentValue"),
                ResolveEvidencePath(evidenceDirectory, ReadRequiredString(item, "trxFile")),
                item.GetProperty("runnerExitCode").GetInt32(),
                item.GetProperty("executed").GetInt32(),
                item.GetProperty("failed").GetInt32(),
                expectedFailed);
        }).ToArray();
        return new ReviewShardEvidence(
            identity,
            platform,
            shardIndex,
            shardCount,
            new ReadOnlyCollection<ReviewProofEvidence>(proofs));
    }

    private static string ResolveEvidencePath(string evidenceDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Evidence report paths must be non-empty and relative.");
        }
        var root = Path.GetFullPath(evidenceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(relativePath, evidenceDirectory);
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            throw new InvalidDataException("Evidence report path escapes its shard or is missing.");
        }
        return resolved;
    }

    private static void ValidateUniqueIdentities(IEnumerable<string> identities)
    {
        var all = identities.ToArray();
        if (all.Any(string.IsNullOrWhiteSpace) ||
            all.Distinct(StringComparer.Ordinal).Count() != all.Length)
        {
            throw new InvalidDataException("CI evidence contains a missing or duplicate identity.");
        }
    }

    private static void ValidateCommit(JsonElement root, string expectedCommitSha)
    {
        if (!string.Equals(
                ReadRequiredString(root, "commitSha"),
                expectedCommitSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("CI evidence belongs to the wrong authoritative SHA.");
        }
    }

    private static JsonDocument ReadJson(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception failure) when (failure is IOException or JsonException)
        {
            throw new InvalidDataException($"Required JSON evidence is missing or malformed: {path}", failure);
        }
    }

    private static void RequireSchema(JsonElement root, int expected, string name)
    {
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != expected)
        {
            throw new InvalidDataException($"{name} has an unsupported schema.");
        }
    }

    private static string ReadRequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Required evidence field '{property}' is empty.");
    }

    private static bool ReadRequiredBoolean(JsonElement element, string property)
    {
        return element.GetProperty(property).GetBoolean();
    }

    private static int ReadPositiveInt(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetInt32();
        return value > 0
            ? value
            : throw new InvalidDataException($"Required evidence field '{property}' must be positive.");
    }

    private static string FormatRepositoryPath(string repositoryRoot, string path) =>
        Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

    private sealed record CiLane(string Platform, string Runner);

    private sealed record BuildEvidence(string Identity, string EvidencePath);

    private sealed record RepositoryShardEvidence(
        string Identity,
        string Platform,
        int ShardIndex,
        int ShardCount,
        IReadOnlyList<RepositoryProjectEvidence> Projects);

    private sealed record RepositoryProjectEvidence(
        string Project,
        string TrxPath,
        int Executed,
        int Failed,
        int ExitCode,
        bool OwnershipEstablished);

    private sealed record ReviewShardEvidence(
        string Identity,
        string Platform,
        int ShardIndex,
        int ShardCount,
        IReadOnlyList<ReviewProofEvidence> Proofs);

    private sealed record ReviewProofEvidence(
        string ProofId,
        string InvariantId,
        string Project,
        string Class,
        string EnvironmentVariable,
        string EnvironmentValue,
        string TrxPath,
        int RunnerExitCode,
        int Executed,
        int Failed,
        int? ExpectedFailedTests);

    private sealed record ExpectedProof(
        string ProofId,
        string InvariantId,
        string Project,
        string Class,
        string EnvironmentVariable,
        string EnvironmentValue,
        int? ExpectedFailedTests);
}

internal enum CiEvidenceAggregatorMutation
{
    None,
    ReturnFalseGreen
}
