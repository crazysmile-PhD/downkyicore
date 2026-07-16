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
    public void TypedInteractionsUseAvaloniaAdaptersWithoutCompatibilityBridges()
    {
        var compositionSource = ReadSource(
            "DownKyi",
            "Composition",
            "DesktopComposition.cs");
        var navigationAdapter = ReadSource(
            "DownKyi",
            "Platform",
            "AvaloniaNavigationService.cs");
        var dialogAdapter = ReadSource(
            "DownKyi",
            "Platform",
            "AvaloniaDialogService.cs");

        Assert.Contains("IUserNotificationService, DesktopNotificationService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IAppNavigationService, AvaloniaNavigationService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IAppDialogService, AvaloniaDialogService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("GetViewModelType", navigationAdapter, StringComparison.Ordinal);
        Assert.Contains("GetDialogTypes", dialogAdapter, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.UIThread.CheckAccess()", dialogAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", compositionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", navigationAdapter, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", dialogAdapter, StringComparison.Ordinal);
    }

    [Fact]
    public void PageItemsAndDownloadAddFlowUseTypedDesktopInteractions()
    {
        var pageItemDirectory = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "PageViewModels");
        var pageItemNames = new[]
        {
            "BangumiFollowMedia.cs",
            "ChannelMedia.cs",
            "FavoritesMedia.cs",
            "FriendInfo.cs",
            "HistoryMedia.cs",
            "PublicationMedia.cs",
            "ToViewMedia.cs"
        };
        var pageItemSource = string.Join(
            Environment.NewLine,
            pageItemNames.Select(name => File.ReadAllText(Path.Combine(pageItemDirectory, name))));
        var downloadSource = string.Join(
            Environment.NewLine,
            new[]
            {
                ReadSource("DownKyi", "Services", "Download", "IAddToDownloadSession.cs"),
                ReadSource("DownKyi", "Services", "Download", "AddToDownloadService.cs"),
                ReadSource("DownKyi", "Services", "Media", "ContentDownloadCoordinator.cs"),
                ReadSource("DownKyi", "Services", "Video", "VideoDetailDownloadCoordinator.cs")
            });

        Assert.Contains("IAppNavigationService", pageItemSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IEventAggregator", pageItemSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigateToView", pageItemSource, StringComparison.Ordinal);
        Assert.Contains("IUserNotificationService", downloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IEventAggregator", downloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageEvent", downloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Threading", downloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NonDialogViewModelsCannotRegainLegacyInteractionDependencies()
    {
        var viewModelDirectory = Path.Combine(RepositoryRoot, "DownKyi", "ViewModels");
        var dialogDirectory = Path.Combine(viewModelDirectory, "Dialogs") + Path.DirectorySeparatorChar;
        var forbiddenTokens = new[]
        {
            "using DownKyi.Events;",
            "using Prism.Dialogs;",
            "using Prism.Events;",
            "DownKyi.PrismExtension.Dialog.IDialogService",
            "IEventAggregator",
            "IRegionManager",
            "MessageEvent",
            "NavigateToView",
            "NavigationEvent",
            "RequestNavigate("
        };
        var violations = Directory
            .EnumerateFiles(viewModelDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(dialogDirectory, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => forbiddenTokens
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, path)}: {token}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DownloadRuntimeUsesTheTypedDialogBoundary()
    {
        var runtimeSource = string.Join(
            Environment.NewLine,
            new[]
            {
                ReadSource("DownKyi", "Services", "Download", "DownloadRuntimeFactory.cs"),
                ReadSource("DownKyi", "Services", "Download", "DownloadService.cs"),
                ReadSource("DownKyi", "Services", "Download", "AriaDownloadService.cs"),
                ReadSource("DownKyi", "Services", "Download", "BuiltinDownloadService.cs"),
                ReadSource("DownKyi", "Services", "Download", "CustomAriaDownloadService.cs")
            });

        Assert.Contains("IAppDialogService", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DownKyi.PrismExtension.Dialog", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IDialogService", runtimeSource, StringComparison.Ordinal);
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
