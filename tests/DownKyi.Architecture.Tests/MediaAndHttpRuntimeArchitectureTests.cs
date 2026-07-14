namespace DownKyi.Architecture.Tests;

public sealed class MediaAndHttpRuntimeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

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
    public void DesktopHostRegistersTheTypedBilibiliClient()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var registrationSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi.Core",
            "BiliApi",
            "BilibiliHttpClientRegistration.cs"));

        Assert.Contains("AddDownKyiBilibiliHttpClient()", appSource, StringComparison.Ordinal);
        Assert.Contains("AddHttpClient<BilibiliHttpClient>", registrationSource, StringComparison.Ordinal);
        Assert.Contains("ConfigurePrimaryHttpMessageHandler", registrationSource, StringComparison.Ordinal);
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
    public void VideoDetailSearchDoesNotMaintainAClonedMediaGraph()
    {
        var paths = new[]
        {
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "ViewVideoDetailViewModel.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoSection.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoPage.cs"),
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels", "PageViewModels", "VideoQuality.cs")
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
