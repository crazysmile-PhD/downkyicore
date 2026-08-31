using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class OwnedProcessSupervisionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] RequiredInvariantKinds =
    [
        "TargetTerminal",
        "RequiredContainment",
        "OperationCompletion",
        "OperationBudget",
        "TreeQuiescence",
        "BoundedCleanup",
        "StreamDrain",
        "OwnershipLifetime"
    ];
    private static readonly string[] RequiredInvariantStates =
    [
        "Unknown",
        "Proven",
        "Violated"
    ];

    [Fact]
    public void OwnedProcessLeaseIsTheOnlyPublicLifecycleAuthority()
    {
        var lease = ReadToolSources("OwnedProcessLease");
        var host = Read("tools/DownKyi.ProcessSupervision/SupervisorHost.cs");
        var hostCapability = Read(
            "tools/DownKyi.ProcessSupervision/PlatformSupervisorHostCapability.cs");
        var router = Read(
            "tools/DownKyi.ProcessSupervision/PlatformProcessContainmentRouter.cs");

        Assert.Contains(
            "public sealed partial class OwnedProcessLease : IAsyncDisposable",
            lease,
            StringComparison.Ordinal);
        Assert.Contains("public static async Task<OwnedProcessLease> StartAsync(", lease,
            StringComparison.Ordinal);
        Assert.Contains("public async Task<OwnedProcessOutcome> WaitAsync(", lease,
            StringComparison.Ordinal);
        Assert.Contains("public async ValueTask DisposeAsync()", lease,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"public\s+[^\r\n]+\s+(Kill|Terminate|Reap|Drain)\w*\s*\(",
                RegexOptions.CultureInvariant),
            lease);

        Assert.Contains("internal static class SupervisorHost", host, StringComparison.Ordinal);
        Assert.Contains(
            "internal sealed class PlatformSupervisorHostCapability",
            hostCapability,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static class PlatformProcessContainmentRouter",
            router,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public static class SupervisorHost", host,
            StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed class PlatformSupervisorHostCapability",
            hostCapability, StringComparison.Ordinal);
    }

    [Fact]
    public void FormalGateIsOnlyTheCompleteTriStateInvariantProof()
    {
        var contracts = Read(
            "tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs");
        var completion = Read(
            "tools/DownKyi.ProcessSupervision/OwnedProcessCompletion.cs");
        var supervisionSources = ReadToolSources();

        Assert.Equal(
            RequiredInvariantKinds,
            ReadEnumMembers(contracts, "OwnedProcessInvariantKind"));
        Assert.Equal(
            RequiredInvariantStates,
            ReadEnumMembers(contracts, "OwnedProcessInvariantState"));
        Assert.Contains(
            "var required = Enum.GetValues<OwnedProcessInvariantKind>()",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains("Invariants.Count == required.Length", contracts,
            StringComparison.Ordinal);
        Assert.Contains(
            "invariant.State == OwnedProcessInvariantState.Proven",
            contracts,
            StringComparison.Ordinal);
        Assert.Contains(") == 1", contracts, StringComparison.Ordinal);
        Assert.Contains(
            "_ => OwnedProcessInvariantState.Unknown",
            completion,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryFailure", supervisionSources, StringComparison.Ordinal);
        Assert.DoesNotContain("FirstCausal", supervisionSources, StringComparison.Ordinal);
        Assert.DoesNotContain("CausalPrecedence", supervisionSources, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalTimeline", supervisionSources, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFactsAndFailuresAreRetainedWithoutCompetingForPrimaryStatus()
    {
        var completion = Read(
            "tools/DownKyi.ProcessSupervision/OwnedProcessCompletion.cs");

        Assert.Contains("_facts.Add(fact)", completion, StringComparison.Ordinal);
        Assert.Contains("_failures.Add(failure)", completion, StringComparison.Ordinal);
        Assert.Contains(".Distinct()", completion, StringComparison.Ordinal);
        Assert.Contains(".OrderBy(fact => fact.Kind)", completion, StringComparison.Ordinal);
        Assert.Contains(".OrderBy(failure => failure.Kind)", completion,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (_states[invariant] != OwnedProcessInvariantState.Violated)",
            completion,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".Clear()", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime", completion, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", completion, StringComparison.Ordinal);
    }

    [Fact]
    public void CallerDeclaresCapabilityAndOneBudgetButCannotSelectABackend()
    {
        var leaseStart = Read(
            "tools/DownKyi.ProcessSupervision/OwnedProcessLease.Start.cs");
        var leaseSources = ReadToolSources("OwnedProcessLease");
        var contracts = Read(
            "tools/DownKyi.ProcessSupervision/ProcessSupervisionContracts.cs");
        var router = Read(
            "tools/DownKyi.ProcessSupervision/PlatformProcessContainmentRouter.cs");

        Assert.Contains("TransitionBudget budget", leaseStart, StringComparison.Ordinal);
        Assert.Contains("ProcessContainmentRequirement containmentRequirement", leaseStart,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessContainmentBackendKind backend", leaseStart,
            StringComparison.Ordinal);
        Assert.Contains("private readonly TransitionBudget _budget", leaseSources,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", leaseSources, StringComparison.Ordinal);
        Assert.Contains("private readonly TimeProvider _timeProvider", contracts,
            StringComparison.Ordinal);
        Assert.Contains("CapturePlatformFacts()", router, StringComparison.Ordinal);
        Assert.Contains("ProcessContainmentRequirement requirement", router,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleScriptConsumesTypedGateAndDoesNotReclassifyFacts()
    {
        var processExecution = Read(
            "script/assembly-lifecycle/process-execution.ps1");
        var forensics = Read(
            "script/assembly-lifecycle/forensics.ps1");
        var classification = Read(
            "script/assembly-lifecycle/result-classification.ps1");

        Assert.Contains("$ownedOutcome.FormalGatePassed", processExecution,
            StringComparison.Ordinal);
        Assert.Contains("New-OwnedProcessProof -Outcome $ownedOutcome", processExecution,
            StringComparison.Ordinal);
        Assert.Contains("function New-OwnedProcessProof", forensics,
            StringComparison.Ordinal);
        Assert.Contains("$Outcome.Invariants", forensics, StringComparison.Ordinal);
        Assert.Contains("$Outcome.Failures", forensics, StringComparison.Ordinal);
        Assert.Contains("$Outcome.Facts", forensics, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?im)^\s*(if|elseif|switch)\b[^\r\n]*\$Outcome\.Facts"),
            forensics);
        Assert.DoesNotContain("$ownedOutcome.Facts", processExecution,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OwnedProcessFactKind", classification,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryFailure", processExecution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticCaptureFailuresCannotEscapeIntoLifecycleControlFlow()
    {
        var processExecution = Read(
            "script/assembly-lifecycle/process-execution.ps1");

        foreach (var reason in new[] { "slow-phase", "slow-exit-after-teardown" })
        {
            var reasonIndex = processExecution.IndexOf(
                $"-Reason \"{reason}\"",
                StringComparison.Ordinal);
            Assert.True(reasonIndex >= 0, $"Missing diagnostic capture branch: {reason}");
            var captureEnd = processExecution.IndexOf(
                "$captureStopwatch.Stop()",
                reasonIndex,
                StringComparison.Ordinal);
            var catchIndex = processExecution.IndexOf(
                "catch {",
                reasonIndex,
                StringComparison.Ordinal);

            Assert.True(captureEnd > reasonIndex, $"Missing capture boundary: {reason}");
            Assert.InRange(catchIndex, reasonIndex + 1, captureEnd - 1);
        }

        Assert.Contains("$exitEvidenceStatus = \"capture-failed\"", processExecution,
            StringComparison.Ordinal);
        Assert.Contains("$exitEvidenceErrorType = $_.Exception.GetType().Name", processExecution,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$marker?.disposed", processExecution,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipAuditRegistersPreciseSubordinateSeams()
    {
        using var document = JsonDocument.Parse(Read(
            "docs/testing/assembly-lifecycle-owners.json"));
        var owners = document.RootElement.GetProperty("owners")
            .EnumerateArray()
            .ToDictionary(
                owner => owner.GetProperty("id").GetString()!,
                StringComparer.Ordinal);

        AssertOwner(
            owners,
            "owned-process-lease",
            [
                "tools/DownKyi.ProcessSupervision/OwnedProcessLease.cs",
                "tools/DownKyi.ProcessSupervision/OwnedProcessLease.Start.cs",
                "tools/DownKyi.ProcessSupervision/OwnedProcessLease.Cleanup.cs"
            ],
            ["static_initialization", "external_process", "host_lifecycle", "sync_wait"]);
        AssertOwner(
            owners,
            "owned-process-supervisor-capability",
            ["tools/DownKyi.ProcessSupervision/PlatformSupervisorHostCapability.cs"],
            ["external_process"]);
        AssertOwner(
            owners,
            "owned-process-containment-router",
            ["tools/DownKyi.ProcessSupervision/PlatformProcessContainmentRouter.cs"],
            ["static_initialization"]);
        AssertOwner(
            owners,
            "owned-process-supervisor-protocol",
            [
                "tools/DownKyi.ProcessSupervision/SupervisorProtocolCodec.cs",
                "tools/DownKyi.ProcessSupervision/SupervisorProtocolState.cs"
            ],
            ["static_initialization"]);
        AssertOwner(
            owners,
            "owned-process-linux-cgroup-authority",
            ["tools/DownKyi.ProcessSupervision/LinuxCgroupContainmentLease.cs"],
            ["synchronous_cleanup"]);

        var registeredPaths = owners.Values
            .SelectMany(owner => owner.GetProperty("paths").EnumerateArray())
            .Select(path => path.GetString())
            .ToArray();
        Assert.DoesNotContain("tools/**", registeredPaths, StringComparer.Ordinal);
        Assert.DoesNotContain(
            "tools/DownKyi.ProcessSupervision/**",
            registeredPaths,
            StringComparer.Ordinal);
    }

    private static void AssertOwner(
        Dictionary<string, JsonElement> owners,
        string id,
        string[] expectedPaths,
        string[] expectedMechanisms)
    {
        var owner = owners[id];
        Assert.Equal(
            expectedPaths,
            owner.GetProperty("paths")
                .EnumerateArray()
                .Select(path => path.GetString())
                .ToArray());
        Assert.Equal(
            expectedMechanisms,
            owner.GetProperty("allowedMechanisms")
                .EnumerateArray()
                .Select(mechanism => mechanism.GetString())
                .ToArray());
        Assert.DoesNotContain(
            "PowerShell",
            owner.GetProperty("owner").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "diagnostic",
            owner.GetProperty("owner").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string[] ReadEnumMembers(string source, string enumName)
    {
        var match = Regex.Match(
            source,
            $@"public enum {Regex.Escape(enumName)}\s*\{{(?<body>[^}}]+)\}}",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Missing enum: {enumName}");
        return match.Groups["body"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ReadToolSources(string? fileNamePrefix = null)
    {
        var toolDirectory = Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision");
        var pattern = fileNamePrefix == null ? "*.cs" : $"{fileNamePrefix}*.cs";
        return string.Join(
            '\n',
            Directory.EnumerateFiles(toolDirectory, pattern, SearchOption.TopDirectoryOnly)
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
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
