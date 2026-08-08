namespace DownKyi.Architecture.Tests;

public sealed class SettingsArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("src", "DownKyi.Desktop", "App.axaml.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "ViewVideoDetailViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Settings", "ViewAboutViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Settings", "ViewBasicViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Settings", "ViewDanmakuViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Settings", "NetworkSettingsCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Settings", "ViewVideoViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "Views", "MainWindow.axaml.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "MainWindowViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "ViewIndexViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "DownloadManager", "ViewDownloadFinishedViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Dialogs", "NewVersionAvailableDialogViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Dialogs", "ViewDownloadSetterViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Dialogs", "ViewParsingSelectorViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Friends", "ViewFollowerViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "Friends", "ViewFollowingViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Account", "UserSessionCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "VideoInfoService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "BangumiInfoService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "CheeseInfoService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadServiceFactory.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Media", "ContentDownloadCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Media", "PersonalMediaCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "FavoritesService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "SearchService.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Video", "VideoParseCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "Video", "VideoDetailWorkflowCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "Presentation", "FavoritesMedia.cs")]
    [InlineData("src", "DownKyi.Desktop", "Presentation", "HistoryMedia.cs")]
    [InlineData("src", "DownKyi.Desktop", "Presentation", "ToViewMedia.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "ViewPublicFavoritesViewModel.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "ViewMySpaceViewModel.cs")]
    [InlineData("DownKyi.Core", "FFmpeg", "FfmpegProcessor.cs")]
    [InlineData("DownKyi.Core", "BiliApi", "Login", "LoginHelper.cs")]
    [InlineData("src", "DownKyi.Desktop", "Services", "UserSpace", "UserSpacePageCoordinator.cs")]
    [InlineData("src", "DownKyi.Desktop", "ViewModels", "ViewUserSpaceViewModel.cs")]
    public void MigratedApplicationOwnersDoNotReachIntoTheSettingsSingleton(params string[] pathParts)
    {
        var source = File.ReadAllText(Path.Combine([RepositoryRoot, .. pathParts]));

        Assert.DoesNotContain("SettingsManager.Instance", source, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostCompositionOwnsOneSettingsStoreRegistration()
    {
        var compositionSource = ReadSource("src", "DownKyi.Desktop", "Composition", "DesktopComposition.cs");

        Assert.Contains("AddSingleton<ISettingsStore, SettingsStore>()", compositionSource, StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(compositionSource, "AddSingleton<ISettingsStore, SettingsStore>()"));
        Assert.DoesNotContain("new SettingsStore", compositionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowRequiresItsSettingsOwnerFromHostComposition()
    {
        var source = ReadSource("src", "DownKyi.Desktop", "Views", "MainWindow.axaml.cs");

        Assert.Contains("MainWindowViewModel viewModel", source, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore settingsStore", source, StringComparison.Ordinal);
        Assert.Contains("IApplicationLifecycle applicationLifecycle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public MainWindow()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkSettingsViewModelOwnsOnlyBindingProjectionAndCommandWiring()
    {
        var viewModelSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.cs");
        var stateSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.State.cs");
        var ariaCommandsSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.AriaCommands.cs");
        var allViewModelSources = string.Concat(viewModelSource, stateSource, ariaCommandsSource);
        var coordinatorSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services",
            "Settings",
            "NetworkSettingsCoordinator.cs");
        var composition = ReadSource("src", "DownKyi.Desktop", "Composition", "DesktopComposition.cs");

        Assert.Contains("INetworkSettingsCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", allViewModelSources, StringComparison.Ordinal);
        Assert.DoesNotContain("IApplicationLifecycle", allViewModelSources, StringComparison.Ordinal);
        Assert.DoesNotContain("AlertService", allViewModelSources, StringComparison.Ordinal);
        Assert.DoesNotContain("Enumerable.Range", allViewModelSources, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryResource", allViewModelSources, StringComparison.Ordinal);
        Assert.All(
            new[] { viewModelSource, stateSource, ariaCommandsSource },
            source => Assert.True(source.Count(character => character == '\n') < 500));
        Assert.Contains("#region 页面属性申明", stateSource, StringComparison.Ordinal);
        Assert.Contains("ExecuteAriaHostCommand", ariaCommandsSource, StringComparison.Ordinal);
        Assert.Contains("ExecuteAriaFileAllocationsCommand", ariaCommandsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteAriaHostCommand", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AriaConfigLogLevel", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ApplyWithRestartPromptAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("INetworkSettingsCoordinator, NetworkSettingsCoordinator", composition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void VideoSettingsViewModelResponsibilitiesRemainSeparated()
    {
        var viewModelSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewVideoViewModel.cs");
        var stateSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewVideoViewModel.State.cs");
        var contentNamingSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewVideoViewModel.ContentNamingCommands.cs");

        Assert.All(
            new[] { viewModelSource, stateSource, contentNamingSource },
            source => Assert.True(source.Count(character => character == '\n') < 500));
        Assert.Contains("ISettingsStore settingsStore", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("OnNavigatedTo", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("VideoQualityCommand", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("FfmpegHardwareAccelerationCommand", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadAllCommand", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_selectedVideoCodec", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("_selectedVideoCodec", stateSource, StringComparison.Ordinal);
        Assert.Contains("SelectedFileName", stateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", stateSource, StringComparison.Ordinal);
        Assert.Contains("ChangeSaveVideoDirectoryCommand", contentNamingSource, StringComparison.Ordinal);
        Assert.Contains("DownloadAllCommand", contentNamingSource, StringComparison.Ordinal);
        Assert.Contains("OrderFormatCommand", contentNamingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", contentNamingSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegProcessorIsOneInjectedCompositionOwner()
    {
        var processorSource = ReadSource("DownKyi.Core", "FFmpeg", "FfmpegProcessor.cs");
        var compositionSource = ReadSource("src", "DownKyi.Desktop", "Composition", "DesktopComposition.cs");

        Assert.DoesNotContain("FfmpegProcessor.Instance", processorSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<FfmpegProcessor>()", compositionSource, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compositionSource, "AddSingleton<FfmpegProcessor>()"));
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
        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
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
        Assert.DoesNotContain("public SettingsStore()", storeSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<SettingsStore>", storeSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<SettingsManager>", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("static SettingsManager Instance", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Lazy<SettingsManager>", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", storeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", managerSource, StringComparison.Ordinal);
        Assert.Contains("File.Replace", managerSource, StringComparison.Ordinal);
        Assert.Contains("ValidateTemporarySettingsFileAsync", managerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new Timer", managerSource, StringComparison.Ordinal);
        Assert.Contains("_scheduledFlushTask", managerSource, StringComparison.Ordinal);
        Assert.Contains("FlushAsync(CancellationToken", managerSource, StringComparison.Ordinal);
        Assert.Contains("switch (settings.SchemaVersion)", migratorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LongRunningOperationsUseExplicitImmutableSettingsSnapshots()
    {
        var utilitySource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services", "Video",
            "VideoPagePlaybackMapper.cs");
        var addSource = ReadSource("src", "DownKyi.Desktop", "Services", "Download", "AddToDownloadService.cs");
        var duplicateSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services", "Download",
            "DownloadDuplicatePolicy.cs");
        var draftSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services", "Download",
            "DownloadTaskDraftFactory.cs");
        var metadataSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services", "Download",
            "DownloadMovieMetadataBuilder.cs");
        var contextFactorySource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services",
            "Download",
            "DownloadExecutionContextFactory.cs");
        var artifactSource = ReadSource("src", "DownKyi.Desktop", "Services", "Download", "DownloadArtifactWriter.cs");
        var diagnosticSource = ReadSource("src", "DownKyi.Desktop", "Services", "Download", "DownloadDiagnosticLogger.cs");
        var ffmpegSource = ReadSource("DownKyi.Core", "FFmpeg", "FfmpegProcessor.cs");

        Assert.Contains("ApplicationSettings settings", utilitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", utilitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("settingsStore.Current", utilitySource, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoPageInfo(playUrl, page, _settingsStore)", addSource, StringComparison.Ordinal);
        Assert.Contains("ApplicationSettings settings", draftSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", duplicateSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", draftSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", metadataSource, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(contextFactorySource, "_settingsStore.Current"));
        Assert.DoesNotContain("ISettingsStore", artifactSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISettingsStore", diagnosticSource, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(ffmpegSource, "_settingsStore.Current.Video.FfmpegMaxParallelJobs"));
        Assert.Equal(1, CountOccurrences(ffmpegSource, "_settingsStore.Current"));
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

    private static int CountOccurrences(string source, string value)
    {
        return source.Split(value, StringSplitOptions.None).Length - 1;
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
