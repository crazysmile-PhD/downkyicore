namespace DownKyi.Architecture.Tests;

public sealed class SettingsArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("DownKyi", "App.axaml.cs")]
    [InlineData("DownKyi", "ViewModels", "ViewVideoDetailViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Settings", "ViewAboutViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Settings", "ViewBasicViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Settings", "ViewDanmakuViewModel.cs")]
    [InlineData("DownKyi", "Services", "Settings", "NetworkSettingsCoordinator.cs")]
    [InlineData("DownKyi", "ViewModels", "Settings", "ViewVideoViewModel.cs")]
    [InlineData("DownKyi", "Views", "MainWindow.axaml.cs")]
    [InlineData("DownKyi", "ViewModels", "MainWindowViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "ViewIndexViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "DownloadManager", "ViewDownloadFinishedViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Dialogs", "NewVersionAvailableDialogViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Dialogs", "ViewDownloadSetterViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Dialogs", "ViewParsingSelectorViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Friends", "ViewFollowerViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "Friends", "ViewFollowingViewModel.cs")]
    [InlineData("DownKyi", "Services", "Account", "UserSessionCoordinator.cs")]
    [InlineData("DownKyi", "Services", "VideoInfoService.cs")]
    [InlineData("DownKyi", "Services", "BangumiInfoService.cs")]
    [InlineData("DownKyi", "Services", "CheeseInfoService.cs")]
    [InlineData("DownKyi", "Services", "Utils.cs")]
    [InlineData("DownKyi", "Services", "Download", "AddToDownloadService.cs")]
    [InlineData("DownKyi", "Services", "Download", "AddToDownloadServiceFactory.cs")]
    [InlineData("DownKyi", "Services", "Media", "ContentDownloadCoordinator.cs")]
    [InlineData("DownKyi", "Services", "Media", "PersonalMediaCoordinator.cs")]
    [InlineData("DownKyi", "Services", "FavoritesService.cs")]
    [InlineData("DownKyi", "Services", "SearchService.cs")]
    [InlineData("DownKyi", "Services", "Video", "VideoParseCoordinator.cs")]
    [InlineData("DownKyi", "Services", "Video", "VideoDetailWorkflowCoordinator.cs")]
    [InlineData("DownKyi", "Utils", "NavigateToView.cs")]
    [InlineData("DownKyi", "ViewModels", "PageViewModels", "FavoritesMedia.cs")]
    [InlineData("DownKyi", "ViewModels", "PageViewModels", "HistoryMedia.cs")]
    [InlineData("DownKyi", "ViewModels", "PageViewModels", "ToViewMedia.cs")]
    [InlineData("DownKyi", "ViewModels", "ViewPublicFavoritesViewModel.cs")]
    [InlineData("DownKyi", "ViewModels", "ViewMySpaceViewModel.cs")]
    [InlineData("DownKyi.Core", "FFMpeg", "FfmpegProcessor.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Login", "LoginHelper.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "WebClient.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "BilibiliHttpClientRegistration.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Sign", "WbiSign.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Video", "VideoInfo.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "VideoStream", "VideoStreamApi.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Users", "UserInfo.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Users", "UserSpace.cs")]
    [InlineData("DownKyi", "Services", "UserSpace", "UserSpaceLoadCoordinator.cs")]
    [InlineData("DownKyi", "Services", "UserSpace", "UserSpacePageCoordinator.cs")]
    [InlineData("DownKyi", "ViewModels", "ViewUserSpaceViewModel.cs")]
    public void MigratedApplicationOwnersDoNotReachIntoTheSettingsSingleton(params string[] pathParts)
    {
        var source = File.ReadAllText(Path.Combine([RepositoryRoot, .. pathParts]));

        Assert.DoesNotContain("SettingsManager.Instance", source, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionRootsShareThePrismOwnedSettingsStore()
    {
        var prismSource = ReadSource("DownKyi", "Composition", "LegacyPrismComposition.cs");
        var hostSource = ReadSource("DownKyi", "Composition", "LegacyDesktopComposition.cs");

        Assert.Contains("RegisterSingleton<ISettingsStore, SettingsStore>()", prismSource, StringComparison.Ordinal);
        Assert.Contains("var settingsStore = container.Resolve<ISettingsStore>()", hostSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(settingsStore)", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new SettingsStore", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRequiresItsSettingsOwnerFromHostComposition()
    {
        var source = ReadSource("DownKyi", "Views", "MainWindow.axaml.cs");

        Assert.Contains("MainWindowViewModel viewModel", source, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore settingsStore", source, StringComparison.Ordinal);
        Assert.Contains("IApplicationLifecycle applicationLifecycle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public MainWindow()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSettingsViewModelOwnsOnlyBindingProjectionAndCommandWiring()
    {
        var viewModelSource = ReadSource(
            "DownKyi",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.cs");
        var stateSource = ReadSource(
            "DownKyi",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.State.cs");
        var coordinatorSource = ReadSource(
            "DownKyi",
            "Services",
            "Settings",
            "NetworkSettingsCoordinator.cs");
        var prismComposition = ReadSource("DownKyi", "Composition", "LegacyPrismComposition.cs");
        var hostComposition = ReadSource("DownKyi", "Composition", "LegacyDesktopComposition.cs");

        Assert.Contains("INetworkSettingsCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IApplicationLifecycle", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AlertService", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.Range", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryResource", viewModelSource, StringComparison.Ordinal);
        Assert.True(viewModelSource.Count(character => character == '\n') < 700);
        Assert.Contains("#region 页面属性申明", stateSource, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ApplyWithRestartPromptAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("INetworkSettingsCoordinator, NetworkSettingsCoordinator", prismComposition,
            StringComparison.Ordinal);
        Assert.Contains("INetworkSettingsCoordinator, NetworkSettingsCoordinator", hostComposition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegProcessorIsOneInjectedCompositionOwner()
    {
        var processorSource = ReadSource("DownKyi.Core", "FFMpeg", "FfmpegProcessor.cs");
        var prismSource = ReadSource("DownKyi", "Composition", "LegacyPrismComposition.cs");
        var hostSource = ReadSource("DownKyi", "Composition", "LegacyDesktopComposition.cs");

        Assert.DoesNotContain("FfmpegProcessor.Instance", processorSource, StringComparison.Ordinal);
        Assert.Contains("RegisterSingleton<FfmpegProcessor>()", prismSource, StringComparison.Ordinal);
        Assert.Contains("container.Resolve<FfmpegProcessor>()", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCodeHasNoDirectSettingsSingletonConsumers()
    {
        var sourceRoots = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi"),
            Path.Combine(RepositoryRoot, "DownKyi.Core"),
            Path.Combine(RepositoryRoot, "src")
        };
        var compatibilityOwner = Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Settings",
            "ISettingsStore.cs");
        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, compatibilityOwner, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("SettingsManager.Instance", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void SettingsStoreKeepsTheValidatedSnapshotAndAtomicPersistenceContract()
    {
        var storeSource = ReadSource("DownKyi.Core", "Settings", "ISettingsStore.cs");
        var managerSource = ReadSource("DownKyi.Core", "Settings", "SettingsManager.cs");
        var migratorSource = ReadSource("DownKyi.Core", "Settings", "SettingsSchemaMigrator.cs");

        Assert.Contains("ApplicationSettings Current", storeSource, StringComparison.Ordinal);
        Assert.Contains("Update(Func<ApplicationSettings, ApplicationSettings>", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsManager Settings", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", storeSource, StringComparison.Ordinal);
        Assert.Contains("SettingsStore(ILoggerFactory loggerFactory)", storeSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<SettingsStore>", storeSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<SettingsManager>", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", managerSource, StringComparison.Ordinal);
        Assert.Contains("File.Replace", managerSource, StringComparison.Ordinal);
        Assert.Contains("FlushAsync(CancellationToken", managerSource, StringComparison.Ordinal);
        Assert.Contains("switch (settings.SchemaVersion)", migratorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCodeCannotReachThroughTheMutableSettingsManager()
    {
        var sourceRoots = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi"),
            Path.Combine(RepositoryRoot, "DownKyi.Core"),
            Path.Combine(RepositoryRoot, "src")
        };
        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("settingsStore.Settings", StringComparison.Ordinal)
                       || source.Contains("_settingsStore.Settings", StringComparison.Ordinal)
                       || source.Contains("SettingsStore.Settings", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    private static string ReadSource(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. pathParts]));
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
