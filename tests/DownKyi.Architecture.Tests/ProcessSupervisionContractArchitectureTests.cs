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
            typeof(ProcessSupervisionOutcome),
            typeof(ProcessContainmentPrimaryFailure),
            typeof(ProcessContainmentBackendResult),
            typeof(ProcessContainmentBackendFailure),
            typeof(ProcessContainmentCallerFailure),
            typeof(ProcessContainmentContractFailure),
            typeof(ProcessContainmentOperationResult),
            typeof(ProcessContainmentOperationCompleted),
            typeof(ProcessContainmentOperationRejected),
            typeof(ProcessContainmentCallerAuthority),
            typeof(ProcessContainmentBackendResultFactory),
            typeof(ProcessContainmentContractGuard),
            typeof(ProcessContainmentOperationAuthority)
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
    public void OperationAuthorityTaxonomyCannotEscalateBackendResults()
    {
        Assert.False(typeof(ProcessContainmentBackendResult).IsAssignableFrom(
            typeof(ProcessContainmentCallerFailure)));
        Assert.False(typeof(ProcessContainmentBackendResult).IsAssignableFrom(
            typeof(ProcessContainmentContractFailure)));
        Assert.False(typeof(ProcessContainmentBackendResult).IsAssignableFrom(
            typeof(ProcessCleanupFailure)));
        Assert.False(typeof(ProcessContainmentCallerFailure).IsAssignableFrom(
            typeof(ProcessContainmentBackendFailure)));
        Assert.False(typeof(ProcessContainmentContractFailure).IsAssignableFrom(
            typeof(ProcessContainmentBackendFailure)));

        var backendFactoryMethods = typeof(ProcessContainmentBackendResultFactory)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType ==
                             typeof(ProcessContainmentBackendResultFactory) &&
                             method.ReturnType ==
                                 typeof(ProcessContainmentBackendResult))
            .ToArray();
        Assert.Equal(
            ["Failed", "Succeeded"],
            backendFactoryMethods
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(backendFactoryMethods, method =>
        {
            Assert.Equal(
                typeof(ProcessContainmentBackendResult),
                method.ReturnType);
            Assert.DoesNotContain(method.GetParameters(), parameter =>
                parameter.ParameterType == typeof(TransitionBudget) ||
                parameter.ParameterType == typeof(CancellationToken) ||
                parameter.ParameterType ==
                    typeof(ProcessContainmentCallerAuthority) ||
                typeof(ProcessContainmentCallerFailure).IsAssignableFrom(
                    parameter.ParameterType) ||
                typeof(ProcessContainmentContractFailure).IsAssignableFrom(
                    parameter.ParameterType));
        });

        var callerFactoryMethods = typeof(ProcessContainmentCallerAuthority)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType ==
                             typeof(ProcessContainmentCallerAuthority) &&
                             method.ReturnType ==
                                 typeof(ProcessContainmentCallerFailure))
            .ToArray();
        Assert.Equal(
            ["PublishCancellation", "PublishDeadlineExceeded"],
            callerFactoryMethods
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(callerFactoryMethods, method => Assert.Equal(
            typeof(ProcessContainmentCallerFailure),
            method.ReturnType));

        var rootFactory = typeof(ProcessContainmentOperationAuthority).GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(rootFactory);
        Assert.Equal(
            [typeof(TransitionBudget), typeof(CancellationToken)],
            rootFactory.GetParameters()
                .Select(parameter => parameter.ParameterType));

        var guardFactories = typeof(ProcessContainmentContractGuard)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType ==
                             typeof(ProcessContainmentContractGuard) &&
                             method.ReturnType ==
                                 typeof(ProcessContainmentContractFailure))
            .ToDictionary(method => method.Name, StringComparer.Ordinal);
        Assert.Equal(
            ["AuthoritySubstitution", "IllegalTransition", "InvalidBackendResult"],
            guardFactories.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            typeof(ProcessContainmentContractFailure),
            guardFactories["IllegalTransition"].ReturnType);
        Assert.Equal(
            typeof(ProcessContainmentContractFailure),
            guardFactories["InvalidBackendResult"].ReturnType);

        var resultEntries = typeof(ProcessContainmentOperationAuthority)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(method => method.DeclaringType ==
                             typeof(ProcessContainmentOperationAuthority) &&
                             method.ReturnType ==
                                 typeof(ProcessContainmentOperationResult))
            .ToArray();
        Assert.Equal(3, resultEntries.Length);
        Assert.Single(resultEntries, method => method.Name == "FromBackend" &&
            method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(
                [
                    typeof(ProcessContainmentBackendResult),
                    typeof(IEnumerable<ProcessCleanupFailure>)
                ]));
        Assert.Equal(
            [
                typeof(ProcessContainmentCallerFailure),
                typeof(ProcessContainmentContractFailure)
            ],
            resultEntries
                .Where(method => method.Name == "Rejected")
                .Select(method => method.GetParameters()[0].ParameterType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.DoesNotContain(resultEntries, method =>
            method.GetParameters()[0].ParameterType ==
                typeof(ProcessContainmentPrimaryFailure));
        Assert.All(resultEntries, method => Assert.Equal(
            typeof(IEnumerable<ProcessCleanupFailure>),
            method.GetParameters()[1].ParameterType));
        Assert.Equal(
            typeof(IReadOnlyList<ProcessCleanupFailure>),
            typeof(ProcessContainmentOperationResult)
                .GetProperty(nameof(ProcessContainmentOperationResult.CleanupFailures))!
                .PropertyType);
        Assert.Equal(
            [
                nameof(ProcessContainmentOperationAuthority.BackendResults),
                nameof(ProcessContainmentOperationAuthority.Caller),
                nameof(ProcessContainmentOperationAuthority.ContractGuard)
            ],
            typeof(ProcessContainmentOperationAuthority)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OperationAuthorityContractsRemainInertAndAreNotRuntimeProof()
    {
        var assembly = typeof(ProcessContainmentPrimaryFailure).Assembly;
        var callerFailures = assembly.GetTypes()
            .Where(type => type != typeof(ProcessContainmentCallerFailure) &&
                           typeof(ProcessContainmentCallerFailure)
                               .IsAssignableFrom(type))
            .ToArray();
        var backendFailures = assembly.GetTypes()
            .Where(type => type != typeof(ProcessContainmentBackendFailure) &&
                           typeof(ProcessContainmentBackendFailure)
                               .IsAssignableFrom(type))
            .ToArray();
        var contractFailures = assembly.GetTypes()
            .Where(type => type != typeof(ProcessContainmentContractFailure) &&
                           typeof(ProcessContainmentContractFailure)
                               .IsAssignableFrom(type))
            .ToArray();

        Assert.Empty(callerFailures);
        Assert.Single(backendFailures);
        Assert.Equal(3, contractFailures.Length);
        Assert.True(typeof(ProcessContainmentCallerFailure).IsSealed);
        Assert.False(typeof(ProcessContainmentCallerFailure).IsAbstract);
        Assert.All(
            typeof(ProcessContainmentCallerFailure).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));
        Assert.Equal(
            [
                nameof(ProcessContainmentCallerFailureKind.Cancellation),
                nameof(ProcessContainmentCallerFailureKind.DeadlineExceeded)
            ],
            Enum.GetNames<ProcessContainmentCallerFailureKind>());
        Assert.DoesNotContain(
            typeof(ProcessContainmentCallerFailure).GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic),
            property => property.Name.Contains(
                "Authority",
                StringComparison.Ordinal));
        Assert.All(
            typeof(ProcessContainmentCallerFailure)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.Name.Contains(
                    "authority",
                    StringComparison.OrdinalIgnoreCase)),
            field => Assert.True(field.IsPrivate));
        Assert.All(
            typeof(ProcessContainmentCallerFailure.Publisher)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.True(field.IsPrivate));
        Assert.DoesNotContain(
            typeof(ProcessContainmentCallerAuthority).GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic),
            property => property.Name.Contains(
                "Identity",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ProcessContainmentOperationAuthority).GetProperties(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic),
            property => property.Name.Contains(
                "Identity",
                StringComparison.Ordinal));
        Assert.All(
            backendFailures.Concat(contractFailures),
            type =>
            {
                Assert.True(type.IsSealed);
                Assert.False(type.IsPublic || type.IsNestedPublic);
                Assert.Empty(type.GetConstructors());
                Assert.False(typeof(ProcessSupervisionProof).IsAssignableFrom(type));
                Assert.False(typeof(EstablishedProcessContainmentFact)
                    .IsAssignableFrom(type));
            });

        Type[] authorityContractTypes =
        [
            typeof(ProcessContainmentCallerAuthority),
            typeof(ProcessContainmentPrimaryFailure),
            typeof(ProcessContainmentBackendResult),
            typeof(ProcessContainmentBackendFailure),
            typeof(ProcessContainmentCallerFailure),
            typeof(ProcessContainmentContractFailure),
            typeof(ProcessContainmentOperationResult),
            typeof(ProcessContainmentOperationCompleted),
            typeof(ProcessContainmentOperationRejected),
            typeof(ProcessContainmentBackendResultFactory),
            typeof(ProcessContainmentContractGuard),
            typeof(ProcessContainmentOperationAuthority)
        ];
        string[] forbiddenRuntimeOwners =
        [
            "Lease",
            "StateMachine",
            "Coordinator",
            "Executor",
            "Host"
        ];
        Assert.DoesNotContain(authorityContractTypes, type =>
            forbiddenRuntimeOwners.Any(fragment =>
                type.Name.Contains(fragment, StringComparison.Ordinal)));
        Assert.DoesNotContain(
            authorityContractTypes.SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly)),
            method => method.Name is "Execute" or "ExecuteAsync" or
                "Start" or "StartAsync" or "Transition");
        Assert.All(authorityContractTypes, type =>
        {
            Assert.False(typeof(ProcessSupervisionProof).IsAssignableFrom(type));
            Assert.False(typeof(EstablishedProcessContainmentFact)
                .IsAssignableFrom(type));
            Assert.False(typeof(IProcessContainmentBackend).IsAssignableFrom(type));
        });
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
