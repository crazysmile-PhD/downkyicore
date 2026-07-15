namespace DownKyi.Architecture.Tests;

public sealed class DesktopInteractionArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopInteractionContractsAreFrameworkNeutral()
    {
        var contractDirectory = Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Application",
            "Desktop");
        var source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(contractDirectory, "*.cs").Select(File.ReadAllText));

        Assert.Contains("IUserNotificationService", source, StringComparison.Ordinal);
        Assert.Contains("IAppNavigationService", source, StringComparison.Ordinal);
        Assert.Contains("IAppDialogService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellAndSearchUseTypedDesktopInteractions()
    {
        var shellSource = ReadSource("DownKyi", "ViewModels", "MainWindowViewModel.cs");
        var searchSource = ReadSource("DownKyi", "Services", "SearchService.cs");

        Assert.Contains("IUserNotificationService", shellSource, StringComparison.Ordinal);
        Assert.Contains("IAppNavigationService", shellSource, StringComparison.Ordinal);
        Assert.Contains("IAppDialogService", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IEventAggregator", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationEvent", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageEvent", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IRegionManager", shellSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IDialogService", shellSource, StringComparison.Ordinal);
        Assert.Contains("AppNavigationRequest", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IEventAggregator", searchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationEvent", searchSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PrismInteractionTypesStayInsideCompatibilityAdaptersAndComposition()
    {
        var compositionSource = ReadSource(
            "DownKyi",
            "Composition",
            "LegacyPrismComposition.cs");
        var navigationAdapter = ReadSource(
            "DownKyi",
            "Platform",
            "PrismNavigationService.cs");
        var dialogAdapter = ReadSource(
            "DownKyi",
            "Platform",
            "PrismDialogService.cs");

        Assert.Contains("IUserNotificationService, DesktopNotificationService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IAppNavigationService, PrismNavigationService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IAppDialogService, PrismDialogService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IRegionManager", navigationAdapter, StringComparison.Ordinal);
        Assert.Contains("LegacyDialogService", dialogAdapter, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
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
