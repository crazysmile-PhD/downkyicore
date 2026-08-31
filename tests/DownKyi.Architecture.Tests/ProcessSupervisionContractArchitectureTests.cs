using System.Reflection;
using System.Xml.Linq;
using DownKyi.ProcessSupervision;

namespace DownKyi.Architecture.Tests;

public sealed class ProcessSupervisionContractArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ContractProjectIsAnInactiveDependencyFreeLibrary()
    {
        var projectPath = Path.Combine(
            RepositoryRoot,
            "tools",
            "DownKyi.ProcessSupervision",
            "DownKyi.ProcessSupervision.csproj");
        var project = XDocument.Load(projectPath);

        Assert.DoesNotContain(
            project.Descendants("OutputType"),
            element => string.Equals(
                element.Value,
                "Exe",
                StringComparison.OrdinalIgnoreCase));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));

        var source = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.GetDirectoryName(projectPath)!,
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));
        string[] forbiddenCapabilities =
        [
            "System.Diagnostics.Process",
            "System.IO.Pipes",
            "System.Runtime.InteropServices",
            "System.Net.Sockets",
            "Microsoft.Extensions.Hosting",
            "OwnedProcessLease",
            "ProcessStartInfo"
        ];
        foreach (var forbidden in forbiddenCapabilities)
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }

        var containmentSource = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(
                    Path.GetDirectoryName(projectPath)!,
                    "*Containment*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));
        string[] forbiddenContainmentOperations =
        [
            "OperatingSystem.",
            "Task.Delay",
            "Thread.Sleep",
            "StartAsync(",
            "WaitAsync(",
            "Terminate(",
            "Kill(",
            "NamedPipe",
            "Socket",
            "HostBuilder"
        ];
        foreach (var forbidden in forbiddenContainmentOperations)
        {
            Assert.DoesNotContain(
                forbidden,
                containmentSource,
                StringComparison.Ordinal);
        }

        var activeExecutionScripts = Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot, "script", "assembly-lifecycle"),
                "*.ps1",
                SearchOption.TopDirectoryOnly)
            .Concat(
            [
                Path.Combine(
                    RepositoryRoot,
                    "script",
                    "test-assembly-lifecycle.ps1"),
                Path.Combine(
                    RepositoryRoot,
                    "script",
                    "test-project-runner.ps1"),
                Path.Combine(
                    RepositoryRoot,
                    "script",
                    "audit-lifecycle-ownership.ps1")
            ]);
        foreach (var script in activeExecutionScripts)
        {
            Assert.DoesNotContain(
                "DownKyi.ProcessSupervision",
                File.ReadAllText(script),
                StringComparison.Ordinal);
        }

        var processExecution = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "script",
            "assembly-lifecycle",
            "process-execution.ps1"));
        Assert.Contains(
            "function Invoke-IsolatedProcess",
            processExecution,
            StringComparison.Ordinal);
        Assert.Contains(
            "[System.Diagnostics.ProcessStartInfo]::new()",
            processExecution,
            StringComparison.Ordinal);

        var supervisionProjectPath = Path.GetFullPath(projectPath);
        var productionReferences = Directory.EnumerateFiles(
                RepositoryRoot,
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(path => !IsUnderDirectory(path, "tests"))
            .Where(path => !IsUnderDirectory(path, "benchmarks"))
            .Where(path => !IsUnderDirectory(path, "bin"))
            .Where(path => !IsUnderDirectory(path, "obj"))
            .SelectMany(path => XDocument.Load(path)
                .Descendants("ProjectReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Select(reference => new
                {
                    Project = Path.GetRelativePath(RepositoryRoot, path),
                    Reference = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(path)!,
                        reference!))
                }))
            .Where(reference => string.Equals(
                reference.Reference,
                supervisionProjectPath,
                StringComparison.OrdinalIgnoreCase))
            .Select(reference => reference.Project)
            .ToArray();
        Assert.Empty(productionReferences);
    }

    [Fact]
    public void PublicProofAndOutcomeValuesCannotBeConstructedOrMutatedByCallers()
    {
        Type[] ownerCreatedTypes =
        [
            typeof(TransitionDeadline),
            typeof(ProcessInvariantEvidence),
            typeof(ProcessInvariantResult),
            typeof(ProcessSupervisionProof),
            typeof(ProcessPrimaryFailure),
            typeof(ProcessCleanupFailure),
            typeof(ProcessTerminalCandidate),
            typeof(ProcessSupervisionOutcome)
        ];

        foreach (var type in ownerCreatedTypes)
        {
            Assert.Empty(type.GetConstructors());
            Assert.All(
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
                property => Assert.True(
                    property.SetMethod is null || !property.SetMethod.IsPublic,
                    $"{type.Name}.{property.Name} exposes a public setter."));
        }
    }

    [Fact]
    public void ContainmentCapabilitySelectionAndRuntimeFactsRemainSeparate()
    {
        var assembly = typeof(ProcessSupervisionProof).Assembly;
        Assert.DoesNotContain(assembly.GetTypes(), type =>
            type.IsClass &&
            !type.IsAbstract &&
            typeof(IProcessContainmentBackend).IsAssignableFrom(type));
        Assert.DoesNotContain(assembly.GetTypes(), type =>
            type.IsClass &&
            !type.IsAbstract &&
            typeof(IProcessContainmentCapabilityProvider).IsAssignableFrom(type));

        Type[] capabilityAndSelectionTypes =
        [
            typeof(ProcessContainmentBackendIdentity),
            typeof(ProcessContainmentCapabilityEvidence),
            typeof(ProcessContainmentCapabilityReport),
            typeof(ProcessContainmentBackendRegistration),
            typeof(ProcessContainmentBackendDiscovery),
            typeof(ProcessContainmentDiscoveryBatch),
            typeof(ProcessContainmentCapabilityDiscoveryCompleted),
            typeof(ProcessContainmentCapabilityDiscoveryFailure),
            typeof(ProcessContainmentCapabilityDiscoveryRejected),
            typeof(ProcessContainmentBackendSelected),
            typeof(ProcessContainmentSelectionFailure),
            typeof(ProcessContainmentBackendRejected),
            typeof(EstablishedProcessContainmentFact)
        ];
        Assert.All(capabilityAndSelectionTypes, type =>
        {
            Assert.False(typeof(ProcessInvariantEvidence).IsAssignableFrom(type));
            Assert.False(typeof(ProcessSupervisionProof).IsAssignableFrom(type));
            if (type != typeof(EstablishedProcessContainmentFact))
            {
                Assert.False(typeof(EstablishedProcessContainmentFact)
                    .IsAssignableFrom(type));
            }

            Assert.Empty(type.GetConstructors());
            Assert.All(
                type.GetProperties(BindingFlags.Instance | BindingFlags.Public),
                property => Assert.True(
                    property.SetMethod is null || !property.SetMethod.IsPublic,
                    $"{type.Name}.{property.Name} exposes a public setter."));
        });

        Assert.Empty(typeof(IProcessContainmentBackend).GetProperties());
        Assert.Empty(typeof(IProcessContainmentBackend).GetMethods());

        var selection = typeof(ProcessContainmentBackendRouter).GetMethod(
            "Select",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(selection);
        Assert.Equal(
            typeof(ProcessContainmentDiscoveryBatch),
            selection.GetParameters()[1].ParameterType);

        Assert.Equal(
            [
                nameof(ProcessContainmentBackendSelected.BackendIdentity),
                nameof(ProcessContainmentBackendSelected.Platform),
                nameof(ProcessContainmentBackendSelected.ExecutionHandle),
                nameof(ProcessContainmentBackendSelected.Capability)
            ],
            typeof(ProcessContainmentBackendSelected)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name));
    }

    [Fact]
    public void PrimaryTaxonomyCannotRepresentCleanupOrDisposeAsTerminal()
    {
        var terminalNames = Enum.GetNames<ProcessTerminalCandidateKind>();
        Assert.DoesNotContain(terminalNames, name =>
            name.Contains("Cleanup", StringComparison.Ordinal) ||
            name.Contains("Dispose", StringComparison.Ordinal) ||
            name.Contains("ResourceRelease", StringComparison.Ordinal));
        Assert.Contains(
            ProcessCleanupFailureKind.ResourceReleaseFailure,
            Enum.GetValues<ProcessCleanupFailureKind>());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException(
                   "Could not locate the DownKyi repository root.");
    }

    private static bool IsUnderDirectory(string path, string directoryName)
    {
        var marker = Path.DirectorySeparatorChar + directoryName +
                     Path.DirectorySeparatorChar;
        return path.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }
}
