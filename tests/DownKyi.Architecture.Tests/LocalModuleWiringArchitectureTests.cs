using System.Reflection;
using DownKyi.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace DownKyi.Architecture.Tests;

public sealed class LocalModuleWiringArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly Assembly DesktopAssembly = typeof(DesktopApplication).Assembly;

    [Fact]
    public void NavigationDialogAndDownloadHideTheirImplementations()
    {
        string[] implementationNames =
        [
            "DownKyi.Platform.AvaloniaNavigationService",
            "DownKyi.Platform.AvaloniaDialogService",
            "DownKyi.Platform.NavigationViewModelFactory",
            "DownKyi.Platform.DialogContentFactory",
            "DownKyi.Services.Download.DownloadTaskQueueGateway",
            "DownKyi.Services.Download.DownloadBootstrapHostedService",
            "DownKyi.Services.Download.DownloadRuntimeFactory",
            "DownKyi.Services.Download.AriaDownloadEmergencyCleanup"
        ];

        var implementations = implementationNames
            .Select(name => DesktopAssembly.GetType(name, throwOnError: true)!)
            .ToArray();

        Assert.All(implementations, implementation => Assert.False(implementation.IsPublic));
    }

    [Fact]
    public void ServiceProviderDependencyIsLimitedToNamedLocalFactories()
    {
        var allowedOwners = new HashSet<string>(StringComparer.Ordinal)
        {
            "DownKyi.Platform.DesktopInteractionComposition",
            "DownKyi.Platform.NavigationViewModelFactory",
            "DownKyi.Platform.DialogContentFactory",
            "DownKyi.Platform.DialogWindow",
            "DownKyi.Services.Download.DownloadComposition"
        };
        var violations = DesktopAssembly.GetTypes()
            .Where(type => type.Namespace is "DownKyi.Platform" or "DownKyi.Services.Download")
            .Where(type => !allowedOwners.Contains(type.FullName!)
                && !type.FullName!.StartsWith(
                    "DownKyi.Services.Download.DownloadComposition+",
                    StringComparison.Ordinal))
            .SelectMany(type => GetDeclaredDependencies(type)
                .Where(IsServiceResolutionType)
                .Select(dependency => $"{type.FullName} -> {dependency.FullName}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void WiringOwnersRejectRepresentativeBypasses()
    {
        var navigationFactory = RequireType("DownKyi.Platform.NavigationViewModelFactory");
        var queueGateway = RequireType("DownKyi.Services.Download.DownloadTaskQueueGateway");

        Assert.False(IsAllowedDependency("DownKyi.ViewModels.ViewIndexViewModel", navigationFactory));
        Assert.False(IsAllowedDependency("DownKyi.Services.Media.ContentDownloadCoordinator", queueGateway));
        Assert.True(IsAllowedDependency("DownKyi.Platform.AvaloniaNavigationService", navigationFactory));
        Assert.True(IsAllowedDependency("DownKyi.Services.Download.DownloadTaskAdmissionService", queueGateway));
    }

    [Fact]
    public void ProductionConsumersUseContractsInsteadOfProtectedImplementations()
    {
        var protectedImplementations = new HashSet<Type>
        {
            RequireType("DownKyi.Platform.AvaloniaNavigationService"),
            RequireType("DownKyi.Platform.AvaloniaDialogService"),
            RequireType("DownKyi.Platform.NavigationViewModelFactory"),
            RequireType("DownKyi.Platform.DialogContentFactory"),
            RequireType("DownKyi.Services.Download.DownloadTaskQueueGateway"),
            RequireType("DownKyi.Services.Download.DownloadBootstrapHostedService"),
            RequireType("DownKyi.Services.Download.DownloadRuntimeFactory"),
            RequireType("DownKyi.Services.Download.AriaDownloadEmergencyCleanup")
        };
        var violations = DesktopAssembly.GetTypes()
            .SelectMany(consumer => GetDeclaredDependencies(consumer)
                .Where(protectedImplementations.Contains)
                .Where(dependency => !IsAllowedDependency(consumer.FullName!, dependency))
                .Select(dependency => $"{consumer.FullName} -> {dependency.FullName}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ProductionConsumersDoNotConstructOrResolveWiringImplementations()
    {
        var protectedTypes = new[]
        {
            "AvaloniaNavigationService",
            "AvaloniaDialogService",
            "NavigationViewModelFactory",
            "DialogContentFactory",
            "DownloadTaskQueueGateway",
            "DownloadBootstrapHostedService",
            "DownloadRuntimeFactory",
            "AriaDownloadEmergencyCleanup"
        };
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DownKyi.Desktop/Platform/DesktopInteractionComposition.cs",
            "src/DownKyi.Desktop/Platform/NavigationViewModelFactory.cs",
            "src/DownKyi.Desktop/Platform/DialogContentFactory.cs",
            "src/DownKyi.Desktop/Services/Download/DownloadComposition.cs"
        };
        var violations = EnumerateProductionSources()
            .Where(item => !allowedFiles.Contains(item.RelativePath))
            .SelectMany(item => protectedTypes
                .Where(typeName => item.Source.Contains($"new {typeName}", StringComparison.Ordinal)
                    || item.Source.Contains($"GetRequiredService<{typeName}>", StringComparison.Ordinal)
                    || item.Source.Contains($"GetService<{typeName}>", StringComparison.Ordinal))
                .Select(typeName => $"{item.RelativePath} -> {typeName}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void LocalModulesDoNotAcquireAContainerOutsideTheirCompositionFactories()
    {
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/DownKyi.Desktop/Platform/DesktopInteractionComposition.cs",
            "src/DownKyi.Desktop/Platform/NavigationViewModelFactory.cs",
            "src/DownKyi.Desktop/Platform/DialogContentFactory.cs",
            "src/DownKyi.Desktop/Services/Download/DownloadComposition.cs"
        };
        string[] forbiddenTokens =
        [
            "IServiceProvider",
            "IServiceCollection",
            "GetRequiredService",
            "GetService<",
            "BuildServiceProvider",
            "ActivatorUtilities",
            "Type.GetType",
            "Assembly.GetType",
            "Resolve<"
        ];
        var violations = EnumerateProductionSources()
            .Where(item => item.RelativePath.StartsWith(
                    "src/DownKyi.Desktop/Platform/",
                    StringComparison.Ordinal)
                || item.RelativePath.StartsWith(
                    "src/DownKyi.Desktop/Services/Download/",
                    StringComparison.Ordinal))
            .Where(item => !allowedFiles.Contains(item.RelativePath))
            .SelectMany(item => forbiddenTokens
                .Where(token => item.Source.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{item.RelativePath} -> {token}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    private static bool IsAllowedDependency(string consumerFullName, Type dependency)
    {
        if (dependency.Namespace == "DownKyi.Services.Download")
        {
            return consumerFullName.StartsWith("DownKyi.Services.Download.", StringComparison.Ordinal);
        }

        return dependency.FullName switch
        {
            "DownKyi.Platform.NavigationViewModelFactory" =>
                consumerFullName is "DownKyi.Platform.AvaloniaNavigationService",
            "DownKyi.Platform.DialogContentFactory" =>
                consumerFullName is "DownKyi.Platform.AvaloniaDialogService",
            "DownKyi.Platform.AvaloniaNavigationService" or
            "DownKyi.Platform.AvaloniaDialogService" =>
                consumerFullName.StartsWith("DownKyi.Platform.", StringComparison.Ordinal),
            _ => false
        };
    }

    private static IEnumerable<Type> GetDeclaredDependencies(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
                                   BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.DeclaredOnly;
        return type.GetFields(Flags).Select(field => field.FieldType)
            .Concat(type.GetProperties(Flags).Select(property => property.PropertyType))
            .Concat(type.GetConstructors(Flags)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType))
            .Concat(type.GetMethods(Flags)
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)));
    }

    private static bool IsServiceResolutionType(Type type)
    {
        return type == typeof(IServiceProvider) || type == typeof(IServiceCollection);
    }

    private static Type RequireType(string fullName) =>
        DesktopAssembly.GetType(fullName, throwOnError: true)!;

    private static IEnumerable<(string RelativePath, string Source)> EnumerateProductionSources()
    {
        var desktopRoot = Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop");
        return Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Select(path => (
                Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText(path)));
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
