using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class MediaAndHttpRuntimeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void VideoMetadataDoesNotCaptureAnOperationTokenInLazyState()
    {
        var videoInfoSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "VideoInfoService.cs"));
        var pageSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "PageViewModels",
            "VideoPage.cs"));
        var addSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Download",
            "AddToDownloadService.cs"));

        Assert.DoesNotContain("LazyTags", videoInfoSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_cancellationToken", videoInfoSource, StringComparison.Ordinal);
        Assert.Contains("LoadTagsAsync = currentToken =>", videoInfoSource, StringComparison.Ordinal);
        Assert.Contains("Func<CancellationToken, Task<IReadOnlyList<string>>> LoadTagsAsync", pageSource,
            StringComparison.Ordinal);
        Assert.Contains("BuildMovieMetadataAsync", addSource, StringComparison.Ordinal);
        Assert.Contains("page.LoadTagsAsync(cancellationToken)", addSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegRuntimeDoesNotRestoreSynchronousProcessWaits()
    {
        var runtimeDirectory = Path.Combine(RepositoryRoot, "DownKyi.Core", "FFMpeg");
        var forbidden = new[]
        {
            "WaitForExit(",
            ".ReadToEnd()",
            "Monitor.Wait",
            ".GetAwaiter().GetResult()"
        };
        var violations = Directory
            .EnumerateFiles(runtimeDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(path => forbidden
                .Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(RepositoryRoot, path)} -> {token}"))
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void FfmpegRuntimeUsesInjectedTypedLogging()
    {
        var runtimeDirectory = Path.Combine(RepositoryRoot, "DownKyi.Core", "FFMpeg");
        var runtimeFiles = Directory
            .EnumerateFiles(runtimeDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .ToArray();
        var violations = runtimeFiles
            .Where(path => File.ReadAllText(path).Contains("LogManager.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var processorSource = File.ReadAllText(Path.Combine(runtimeDirectory, "FfmpegProcessor.cs"));
        var concatSource = File.ReadAllText(Path.Combine(runtimeDirectory, "FfmpegConcatRuntime.cs"));
        var detectorSource = File.ReadAllText(Path.Combine(runtimeDirectory, "FfmpegHardwareEncoderDetector.cs"));

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("ILoggerFactory loggerFactory", processorSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<FfmpegConcatRuntime> logger", concatSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<FfmpegHardwareEncoderDetector> logger", detectorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("static class FfmpegHardwareEncoderDetector", detectorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopHostRegistersTheTypedBilibiliClient()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "DesktopComposition.cs"));
        var registrationSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "BilibiliHttpClientRegistration.cs"));

        Assert.DoesNotContain("AddDownKyiBilibiliHttpClient()", appSource, StringComparison.Ordinal);
        Assert.Contains("AddDownKyiBilibiliHttpClient()", compositionSource, StringComparison.Ordinal);
        Assert.Contains("AddHttpClient<BilibiliHttpClient>", registrationSource, StringComparison.Ordinal);
        Assert.Contains("ConfigurePrimaryHttpMessageHandler", registrationSource, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore settingsStore", registrationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BilibiliCoreLeavesSanitizedDiagnosticsToInjectedCoordinators()
    {
        var apiDirectory = Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi");
        var violations = Directory
            .EnumerateFiles(apiDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("LogManager.", StringComparison.Ordinal)
                       || source.Contains("Console.Print", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        var webClientSource = File.ReadAllText(Path.Combine(apiDirectory, "WebClient.cs"));
        var loginCoordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Account",
            "LoginCoordinator.cs"));
        var userSpaceCoordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "UserSpace",
            "UserSpacePageCoordinator.cs"));

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
        Assert.Contains("GetConfiguredClient().Send(", webClientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("catch (Exception", webClientSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<LoginCoordinator>", loginCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<UserSpacePageCoordinator>", userSpaceCoordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BilibiliApiBoundaryUsesTypedFailuresInsteadOfNullFallbacks()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "BiliApiRequest.cs"));

        Assert.Contains("BilibiliApiResponseException", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return default", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WbiSigningUsesExplicitRuntimeKeysInsteadOfSettingsSnapshots()
    {
        var signSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "Sign",
            "WbiSign.cs"));
        var endpointPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Video", "VideoInfo.cs"),
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "VideoStream", "VideoStreamApi.cs"),
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Users", "UserInfo.cs"),
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Users", "UserSpace.cs")
        };

        Assert.DoesNotContain("ISettingsStore", signSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.Now", signSource, StringComparison.Ordinal);
        foreach (var path in endpointPaths)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("ISettingsStore", source, StringComparison.Ordinal);
            Assert.Contains("WbiKeys", source, StringComparison.Ordinal);
        }

        var navigationModel = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "Users",
            "Models",
            "UserInfoForNavigation.cs"));
        Assert.Contains("public Wbi? Wbi", navigationModel, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonEnvelopeFieldsCannotHideMissingPayloadsWithDefaultInitializers()
    {
        const string defaultEnvelopePattern =
            @"\[JsonProperty(?:Name)?\(\""(?:data|result|error|payload)\""\)\]\s*" +
            @"public[^\{\r\n]+\{\s*get;\s*set;\s*\}\s*=\s*(?:new\b|Array\.Empty)";
        var roots = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi"),
            Path.Combine(RepositoryRoot, "DownKyi.Core", "Aria2cNet", "Client", "Entity")
        };
        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                defaultEnvelopePattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(2)))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void VideoDetailSearchDoesNotMaintainAClonedMediaGraph()
    {
        var paths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewVideoDetailViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoSection.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoPage.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoQuality.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "Video", "VideoDetailWorkflowCoordinator.cs")
        };
        var source = string.Join(Environment.NewLine, paths.Select(File.ReadAllText));

        Assert.DoesNotContain("CaCheVideoSections", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CloneForCache", source, StringComparison.Ordinal);
        Assert.Contains("VideoSearchState", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoDetailViewModelDoesNotOwnAvaloniaControls()
    {
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewVideoDetailViewModel.cs"));
        var viewSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewVideoDetail.axaml"));

        Assert.DoesNotContain("Avalonia.Controls", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DataGrid", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetGridSplitterBehavior", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("VideoPageSelectionBehavior", viewSource, StringComparison.Ordinal);
        Assert.Contains("ResetGridSplitterBehavior", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoDetailViewModelRetainsOnlyBindingCommandsNavigationAndProjection()
    {
        var viewModelPath = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewVideoDetailViewModel.cs");
        var viewModelSource = File.ReadAllText(viewModelPath);
        var workflowSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Video",
            "VideoDetailWorkflowCoordinator.cs"));
        var downloadSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Video",
            "VideoDetailDownloadCoordinator.cs"));
        var viewSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewVideoDetail.axaml"));

        Assert.True(File.ReadLines(viewModelPath).Count() <= 425, "Video-detail ViewModel exceeded its size budget.");
        Assert.Contains("IVideoDetailWorkflowCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IVideoDetailDownloadCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoParseCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("VideoSearchState", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IAddToDownloadServiceFactory", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationTokenSource", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Regex.Replace", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("VideoParseCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("VideoSearchState", workflowSource, StringComparison.Ordinal);
        Assert.Contains("DownloadAddCoordinator", downloadSource, StringComparison.Ordinal);
        Assert.Contains("UiState.VideoInfoView", viewSource, StringComparison.Ordinal);
        Assert.Contains("UiState.IsSelectAll", viewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoDetailServicesDoNotQueuePartiallyBuiltViews()
    {
        var servicePaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "VideoInfoService.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "BangumiInfoService.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "CheeseInfoService.cs")
        };

        foreach (var path in servicePaths)
        {
            var source = File.ReadAllText(path);
            Assert.DoesNotContain("App.PropertyChangeAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Dispatcher.UIThread", source, StringComparison.Ordinal);
        }

        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewVideoDetailViewModel.cs"));
        Assert.Contains("LoadDetailAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("UiDispatcher.InvokeAsync", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BiliHelperCpuWorkIsCancellableAndOutsideTheViewModel()
    {
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "Toolbox",
            "ViewBiliHelperViewModel.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Toolbox",
            "BiliHelperCoordinator.cs"));
        var coreSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "BiliUtils",
            "DanmakuSender.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IBiliHelperCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", coreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AccountNetworkWorkIsCancellableAndOutsideViewModels()
    {
        var viewModelPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewIndexViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewLoginViewModel.cs")
        };
        var coordinatorPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "Account", "UserSessionCoordinator.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "Services", "Account", "LoginCoordinator.cs")
        };

        foreach (var path in viewModelPaths)
        {
            Assert.DoesNotContain("Task.Run", File.ReadAllText(path), StringComparison.Ordinal);
        }

        var coordinatorSource = string.Join(
            Environment.NewLine,
            coordinatorPaths.Select(File.ReadAllText));
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("IUserSessionCoordinator", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ILoginCoordinator", coordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendRelationWorkReturnsSnapshotsAndBatchesUiProjection()
    {
        var viewModelPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "Friends", "ViewFollowingViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "Friends", "ViewFollowerViewModel.cs")
        };
        var viewModelSource = string.Join(
            Environment.NewLine,
            viewModelPaths.Select(File.ReadAllText));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Friends",
            "FriendRelationCoordinator.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyChangeAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Contents.AddRange", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CurrentChanging -=", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("OnNavigatedFrom", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CancelAndDispose(ref _loadCancellation)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = true", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SeasonsSeriesWorkUsesOneCancellableSnapshotPipeline()
    {
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewSeasonsSeriesViewModel.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "UserSpace",
            "SeasonsSeriesCoordinator.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChangeAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSeasonsAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSeriesAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateChannelAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Medias.AddRange", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ISeasonsSeriesCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IAddToDownloadServiceFactory", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDirectory", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("_downloadCoordinator.AddAsync", coordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FavoritesWorkUsesCancellableSnapshotsAndSharedDownloadCoordination()
    {
        var viewModelPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewMyFavoritesViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewPublicFavoritesViewModel.cs")
        };
        var viewModelSource = string.Join(Environment.NewLine, viewModelPaths.Select(File.ReadAllText));
        var favoritesServiceSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "FavoritesService.cs"));
        var favoritesCoordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "FavoritesCoordinator.cs"));
        var downloadCoordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Media",
            "ContentDownloadCoordinator.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChangeAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChangeAsync", favoritesServiceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ObservableCollection", favoritesServiceSource, StringComparison.Ordinal);
        Assert.Contains("AddRange", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IFavoritesCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IContentDownloadCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", favoritesCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", favoritesCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", downloadCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("_serviceFactory.Create", downloadCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("DownloadAddCoordinator", downloadCoordinatorSource, StringComparison.Ordinal);

        foreach (var source in viewModelPaths.Select(File.ReadAllText))
        {
            Assert.DoesNotContain("IAddToDownloadServiceFactory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetDirectory", source, StringComparison.Ordinal);
            Assert.Contains("_downloadCoordinator.AddAsync(", source, StringComparison.Ordinal);
        }

        var publicView = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewPublicFavorites.axaml"));
        var privateView = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewMyFavorites.axaml"));
        Assert.Contains("SelectionMode=\"Multiple,Toggle\"", publicView, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Multiple,Toggle\"", privateView, StringComparison.Ordinal);
    }

    [Fact]
    public void PersonalMediaPagesUseCancellableSnapshotsAndSharedDownloadCoordination()
    {
        var viewModelPaths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewMyToViewVideoViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewMyHistoryViewModel.cs")
        };
        var viewModelSource = string.Join(Environment.NewLine, viewModelPaths.Select(File.ReadAllText));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Media",
            "PersonalMediaCoordinator.cs"));
        var toViewApiSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "History",
            "ToView.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChangeAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IPersonalMediaCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IContentDownloadCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddRange", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken cancellationToken = default", toViewApiSource, StringComparison.Ordinal);
        Assert.Contains("cancellationToken);", toViewApiSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadMoreCommand => new(", viewModelSource, StringComparison.Ordinal);

        foreach (var source in viewModelPaths.Select(File.ReadAllText))
        {
            Assert.DoesNotContain("IAddToDownloadServiceFactory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetDirectory", source, StringComparison.Ordinal);
            Assert.Contains("_downloadCoordinator.AddAsync(", source, StringComparison.Ordinal);
        }

        foreach (var viewName in new[] { "ViewMyToViewVideo.axaml", "ViewMyHistory.axaml" })
        {
            var viewSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "Views", viewName));
            Assert.Contains("SelectionMode=\"Multiple,Toggle\"", viewSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UserSpacePagesUseCancellableSnapshotsWithoutWorkerThreadUiMutation()
    {
        var publicationPath = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewPublicationViewModel.cs");
        var publicationSearchPath = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewPublicationViewModel.Search.cs");
        var mySpacePath = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewMySpaceViewModel.cs");
        var bangumiPath = Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "ViewMyBangumiFollowViewModel.cs");
        var publicationSource = string.Join(
            Environment.NewLine,
            File.ReadAllText(publicationPath),
            File.ReadAllText(publicationSearchPath));
        var viewModelSource = string.Join(
            Environment.NewLine,
            publicationSource,
            File.ReadAllText(mySpacePath),
            File.ReadAllText(bangumiPath));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "UserSpace",
            "UserSpacePageCoordinator.cs"));

        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.PropertyChangeAsync", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyChangeAsync(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IUserSpacePageCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IContentDownloadCoordinator", File.ReadAllText(publicationPath), StringComparison.Ordinal);
        Assert.Contains("Medias.AddRange", publicationSource, StringComparison.Ordinal);
        Assert.Contains("CurrentChanging -=", publicationSource, StringComparison.Ordinal);
        Assert.Contains("LoadMyProfileAsync", File.ReadAllText(mySpacePath), StringComparison.Ordinal);
        Assert.Contains("LoadMyStatsAsync", File.ReadAllText(mySpacePath), StringComparison.Ordinal);
        Assert.Contains("LoadBangumiFollowPageAsync", File.ReadAllText(bangumiPath), StringComparison.Ordinal);
        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);

        foreach (var source in new[] { File.ReadAllText(publicationPath), File.ReadAllText(bangumiPath) })
        {
            Assert.DoesNotContain("IAddToDownloadServiceFactory", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SetDirectory", source, StringComparison.Ordinal);
            Assert.Contains("_downloadCoordinator.AddAsync(", source, StringComparison.Ordinal);
        }

        var publicationView = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewPublication.axaml"));
        Assert.Contains("SelectionMode=\"Multiple,Toggle\"", publicationView, StringComparison.Ordinal);
        var bangumiView = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "ViewMyBangumiFollow.axaml"));
        Assert.Contains("SelectionMode=\"Multiple,Toggle\"", bangumiView, StringComparison.Ordinal);

        foreach (var apiPath in new[]
                 {
                     Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Users", "UserSpace.cs"),
                     Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Users", "UserInfo.cs"),
                     Path.Combine(RepositoryRoot, "DownKyi.Core", "BiliApi", "Users", "UserStatus.cs")
                 })
        {
            Assert.Contains("CancellationToken cancellationToken = default", File.ReadAllText(apiPath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LegacyUpgradeDialogDelegatesMigrationAndOwnsCancellation()
    {
        var viewModelSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "Dialogs",
            "ViewUpgradingDialogViewModel.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Services",
            "Migration",
            "LegacyUpgradeCoordinator.cs"));

        Assert.Contains("ILegacyUpgradeCoordinator", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CancelUpgrade();", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NrbfDecoder", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SqliteDatabase", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageManager", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.UIThread", viewModelSource, StringComparison.Ordinal);

        Assert.Contains("Task.Run", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CancellationToken", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("using var database", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("AddDownloadedBatchAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<LegacyUpgradeCoordinator>", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", coordinatorSource, StringComparison.Ordinal);

        var databaseSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "Storage",
            "Database",
            "SqliteDatabase.cs"));
        Assert.DoesNotContain("LogManager.", databaseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.", databaseSource, StringComparison.Ordinal);
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
