using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DownKyi.Architecture.Tests;

public sealed class AssemblyLifecycleReleaseEvidenceTests
{
    private const string ExpectedCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AssemblyName = "DownKyi.Architecture.Tests";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ValidatorPath = Path.Combine(
        RepositoryRoot,
        "script",
        "assert-assembly-lifecycle-release-evidence.ps1");

    [Fact]
    public void CompleteExactHeadShardsAndMutationSelfTestsPass()
    {
        using var fixture = new EvidenceFixture();

        var result = RunValidator(fixture.Root, fixture.OutputPath, validateMutations: true);

        Assert.True(result.ExitCode == 0, result.Output);
        using var aggregate = JsonDocument.Parse(File.ReadAllText(fixture.OutputPath));
        Assert.True(aggregate.RootElement.GetProperty("successful").GetBoolean());
        Assert.Equal(100, aggregate.RootElement.GetProperty("totalIterations").GetInt32());
        Assert.Equal(16, aggregate.RootElement.GetProperty("shards").GetArrayLength());
        Assert.Equal(4, aggregate.RootElement.GetProperty("mutationSelfTests").GetArrayLength());
    }

    [Fact]
    public void StaleShardCommitFailsClosed()
    {
        using var fixture = new EvidenceFixture();
        fixture.ReplaceManifestValue(0, "commitSha", new string('b', 40));

        var result = RunValidator(fixture.Root, fixture.OutputPath);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void CopiedShardReportFailsClosedEvenWhenItsManifestHashIsUpdated()
    {
        using var fixture = new EvidenceFixture();
        var firstReport = fixture.ReportPath(0);
        var secondReport = fixture.ReportPath(1);
        File.Copy(firstReport, secondReport, overwrite: true);
        fixture.ReplaceManifestValue(1, "reportSha256", HashFile(secondReport));

        var result = RunValidator(fixture.Root, fixture.OutputPath);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void MissingShardFailsClosed()
    {
        using var fixture = new EvidenceFixture();
        Directory.Delete(fixture.ShardRoot(15), recursive: true);

        var result = RunValidator(fixture.Root, fixture.OutputPath);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void MissingForensicsProofFailsClosed()
    {
        using var fixture = new EvidenceFixture();
        fixture.ReplaceReportValue(2, "forensicsEvidencePersistenceSelfTestPassed", false);
        fixture.ReplaceManifestValue(2, "reportSha256", HashFile(fixture.ReportPath(2)));

        var result = RunValidator(fixture.Root, fixture.OutputPath);

        Assert.NotEqual(0, result.ExitCode);
    }

    private static BoundedProcessResult RunValidator(
        string evidenceRoot,
        string outputPath,
        bool validateMutations = false)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile",
                     "-NonInteractive",
                     "-File",
                     ValidatorPath,
                     "-EvidenceRoot",
                     evidenceRoot,
                     "-ExpectedAssembly",
                     AssemblyName,
                     "-ExpectedCommitSha",
                     ExpectedCommit,
                     "-OutputPath",
                     outputPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (validateMutations)
        {
            startInfo.ArgumentList.Add("-ValidateMutationSelfTests");
        }

        return BoundedProcessRunner.Run(
            startInfo,
            TestContext.Current.CancellationToken,
            TimeSpan.FromSeconds(30));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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

    private sealed class EvidenceFixture : IDisposable
    {
        private static readonly string[] RequiredPhases =
        [
            "load",
            "assembly-info",
            "discovery",
            "execution",
            "assembly-teardown",
            "process-exit"
        ];

        public EvidenceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"downkyi-lifecycle-release-{Guid.NewGuid():N}");
            OutputPath = Path.Combine(Root, "aggregate", "aggregate-manifest.json");
            Directory.CreateDirectory(Root);
            for (var shardIndex = 0; shardIndex < 16; shardIndex++)
            {
                WriteShard(shardIndex, shardIndex < 4 ? 7 : 6);
            }
        }

        public string Root { get; }

        public string OutputPath { get; }

        public string ShardRoot(int index) => Path.Combine(Root, $"shard-{index:D2}");

        public string ReportPath(int index) => Path.Combine(
            ShardRoot(index),
            "assembly-lifecycle-report.json");

        public void ReplaceManifestValue(int index, string property, object value)
        {
            ReplaceJsonValue(Path.Combine(ShardRoot(index), "shard-manifest.json"), property, value);
        }

        public void ReplaceReportValue(int index, string property, object value)
        {
            ReplaceJsonValue(ReportPath(index), property, value);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private void WriteShard(int shardIndex, int iterations)
        {
            var shardRoot = ShardRoot(shardIndex);
            Directory.CreateDirectory(shardRoot);
            var results = Enumerable.Range(1, iterations)
                .SelectMany(iteration => RequiredPhases.Select(phase => new
                {
                    assembly = AssemblyName,
                    iteration,
                    phase,
                    success = true,
                    residualChildCount = 0
                }))
                .ToArray();
            var report = new
            {
                schemaVersion = 4,
                generatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(shardIndex).ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture),
                profile = "Rehearsal",
                iterations,
                commitSha = ExpectedCommit,
                workingTreeDirty = false,
                testAssemblyCount = 1,
                successful = true,
                failedPhaseCount = 0,
                slowEvidenceMissingCount = 0,
                residualChildPhaseCount = 0,
                residualChildObservedCount = 0,
                residualChildEvidenceMissingCount = 0,
                forensicsSelfTestCaptureLeadValidated = true,
                forensicsSelfTestPositiveCaptureThresholdValidated = true,
                forensicsSelfTestCaptureCompletedBeforeTargetExitValidated = true,
                forensicsSelfTestEvidenceHoldValidated = true,
                forensicsSelfTestReleaseOrderingMutationValidated = true,
                slowEvidenceOrderingSelfTestPassed = true,
                reporterContractSelfTestPassed = true,
                forensicsCollectorCaptureWindowSelfTestPassed = true,
                forensicsCollectorCleanupReportSelfTestPassed = true,
                forensicsEvidencePersistenceSelfTestPassed = true,
                forensicsCollectorInterruptedStackSelfTestPassed = true,
                dotnetStackAttachStallSelfTestPassed = true,
                ownershipAuditPassed = true,
                markerReaderSelfTestPassed = true,
                processLeaseSelfTestPassed = true,
                results
            };
            var reportPath = ReportPath(shardIndex);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report));
            var manifest = new
            {
                schemaVersion = 1,
                kind = "assembly-lifecycle-release-shard",
                evidenceId = $"{AssemblyName}/{shardIndex}-of-16",
                commitSha = ExpectedCommit,
                assembly = AssemblyName,
                profile = "Rehearsal",
                validateForensics = true,
                shardIndex,
                shardCount = 16,
                totalIterations = 100,
                shardIterations = iterations,
                reportRelativePath = "assembly-lifecycle-report.json",
                reportSha256 = HashFile(reportPath),
                successful = true
            };
            File.WriteAllText(
                Path.Combine(shardRoot, "shard-manifest.json"),
                JsonSerializer.Serialize(manifest));
        }

        private static void ReplaceJsonValue(string path, string property, object value)
        {
            var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            root[property] = JsonValue.Create(value);
            File.WriteAllText(path, root.ToJsonString());
        }
    }
}
