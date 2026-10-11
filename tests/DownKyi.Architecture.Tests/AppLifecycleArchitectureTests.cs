namespace DownKyi.Architecture.Tests;

public sealed class AppLifecycleArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void WindowsDesktopStartupHasOneExplicitStaUiThreadOwner()
    {
        var programSource = ReadSource("DownKyi", "Program.cs");
        var desktopSource = ReadSource("src", "DownKyi.Desktop", "DesktopApplication.cs");

        Assert.Contains("public static Task Main", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("[STAThread]", programSource, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", desktopSource, StringComparison.Ordinal);
        Assert.Contains("RunOnWindowsStaThreadAsync", desktopSource, StringComparison.Ordinal);
        Assert.Contains("SetApartmentState(ApartmentState.STA)", desktopSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OleInitialize", desktopSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitPathDoesNotSynchronouslyWaitForAsyncCleanup()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml.cs");
        var desktopSource = ReadSource("src", "DownKyi.Desktop", "DesktopApplication.cs");
        var lifecycleSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Platform",
            "AvaloniaApplicationLifecycle.cs");

        Assert.DoesNotContain(".GetAwaiter().GetResult()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", desktopSource, StringComparison.Ordinal);
        Assert.Contains("AvaloniaApplicationLifecycle", appSource, StringComparison.Ordinal);
        Assert.Contains("host.StopAsync(CancellationToken.None)", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("_settingsStore.FlushAsync", lifecycleSource, StringComparison.Ordinal);
        var disposeStart = appSource.IndexOf("private async Task DisposeCoreAsync()", StringComparison.Ordinal);
        var createHostStart = appSource.IndexOf("private void CreateHost()", StringComparison.Ordinal);
        Assert.True(disposeStart >= 0 && createHostStart > disposeStart);
        Assert.DoesNotContain("Task.Run", appSource[disposeStart..createHostStart], StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowHidesImmediatelyAndExplicitExitOwnsCleanup()
    {
        var windowSource = ReadSource("src", "DownKyi.Desktop", "Views", "MainWindow.axaml.cs");
        var contextSource = ReadSource("src", "DownKyi.Desktop", "Platform", "AvaloniaDesktopContext.cs");
        var lifecycleSource = ReadSource(
            "src", "DownKyi.Desktop", "Platform", "AvaloniaApplicationLifecycle.cs");

        Assert.Contains("e.Cancel = true", windowSource, StringComparison.Ordinal);
        Assert.Contains("Hide();", windowSource, StringComparison.Ordinal);
        Assert.Contains("PrepareForApplicationExit", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestShutdownAsync", windowSource, StringComparison.Ordinal);
        Assert.Contains("_prepareForShutdown?.Invoke()", contextSource, StringComparison.Ordinal);
        Assert.Contains("await RequestShutdownAsync(cancellationToken)", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("await _desktopContext.ShutdownAsync()", lifecycleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstVisibleWindowDoesNotOwnHostOrIndexInitialization()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml.cs");
        var prepareIndex = appSource.IndexOf("PrepareForFirstShow()", StringComparison.Ordinal);
        var completeFrameworkInitialization = appSource.IndexOf(
            "base.OnFrameworkInitializationCompleted()",
            StringComparison.Ordinal);

        Assert.True(prepareIndex >= 0 && prepareIndex < completeFrameworkInitialization);
        Assert.Contains("Task.Run(applicationLifecycle.StartHostAsync)", appSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRestartPromptsCannotBypassAsynchronousCleanup()
    {
        var viewModelSource = ReadSource(
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.cs");
        var coordinatorSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services",
            "Settings",
            "NetworkSettingsCoordinator.cs");

        Assert.DoesNotContain("IApplicationLifecycle", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IClassicDesktopStyleApplicationLifetime", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("_applicationLifecycle.RestartAsync(cancellationToken)", coordinatorSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModelsCannotUseStaticApplicationOrProcessLifecycle()
    {
        var files = Directory.GetFiles(
            Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "ViewModels"),
            "*.cs",
            SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("App.Current", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.GetCurrentProcess", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IClassicDesktopStyleApplicationLifetime", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppDelegatesHostShutdownRestartAndSingleInstanceOwnership()
    {
        var appSource = ReadSource("src", "DownKyi.Desktop", "App.axaml.cs");
        var desktopSource = ReadSource("src", "DownKyi.Desktop", "DesktopApplication.cs");
        var lifecycleSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Platform",
            "AvaloniaApplicationLifecycle.cs");
        var restartSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Platform",
            "ProcessRestartLauncher.cs");

        Assert.Contains("SingleInstanceGuard.TryAcquire", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SingleInstanceGuard.TryAcquire", appSource, StringComparison.Ordinal);
        Assert.Contains("_applicationLifecycle.AttachHost", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new Mutex", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StopHostAsync", appSource, StringComparison.Ordinal);
        var disposeStart = appSource.IndexOf("private async Task DisposeCoreAsync()", StringComparison.Ordinal);
        var createHostStart = appSource.IndexOf("private void CreateHost()", StringComparison.Ordinal);
        Assert.True(disposeStart >= 0 && createHostStart > disposeStart);
        Assert.DoesNotContain("FlushAsync", appSource[disposeStart..createHostStart], StringComparison.Ordinal);
        Assert.Contains("IProcessRestartLauncher", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync", restartSource, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", restartSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSingleInstanceGuardPrecedesHostAndStorageInitialization()
    {
        var desktopSource = ReadSource("src", "DownKyi.Desktop", "DesktopApplication.cs");
        var runStart = desktopSource.IndexOf("public static async Task RunAsync", StringComparison.Ordinal);
        var runEnd = desktopSource.IndexOf(
            "internal static Task RunOnWindowsStaThreadAsync", StringComparison.Ordinal);
        Assert.True(runStart >= 0 && runEnd > runStart);

        var run = desktopSource[runStart..runEnd];
        var productionGuard = run.IndexOf("#if !DEBUG", StringComparison.Ordinal);
        var acquire = run.IndexOf("SingleInstanceGuard.TryAcquire", StringComparison.Ordinal);
        var rejectedReturn = run.IndexOf("return;", acquire, StringComparison.Ordinal);
        var buildAvalonia = run.IndexOf("BuildAvaloniaApp()", StringComparison.Ordinal);

        Assert.True(productionGuard >= 0 && productionGuard < acquire);
        Assert.True(acquire < rejectedReturn && rejectedReturn < buildAvalonia);
        Assert.DoesNotContain("AppContext.BaseDirectory", run, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDoesNotOwnGlobalDownloadCollections()
    {
        var source = ReadSource("src", "DownKyi.Desktop", "App.axaml.cs");

        Assert.DoesNotContain("static ImmutableObservableCollection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingList { get;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadedList { get;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadListState", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostOwnsDownloadBootstrapAndRuntimeLifecycle()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "App.axaml.cs"));
        var downloadCompositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Services", "Download",
            "DownloadComposition.cs"));

        Assert.Contains("DownKyiHost.Create", appSource, StringComparison.Ordinal);
        Assert.Contains("AddDownKyiDesktop", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("host.StopAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("DownloadBootstrapHostedService", downloadCompositionSource, StringComparison.Ordinal);
        Assert.Contains("IDownloadRuntimeFactory", downloadCompositionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadDownloadStateAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingHistoryAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingDownloadHistoryAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDownloadService", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadingAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadedAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadService", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDelegatesAllRegistrationsToMicrosoftDiComposition()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Platform",
            "DesktopInteractionComposition.cs"));

        Assert.Contains("services.AddDownKyiDesktop", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterForNavigation", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterDialog", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterSingleton", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DryIoc", appSource, StringComparison.Ordinal);
        Assert.Contains("IAppNavigationService, AvaloniaNavigationService", compositionSource,
            StringComparison.Ordinal);
        Assert.Contains("IAppDialogService, AvaloniaDialogService", compositionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationLoggingUsesOneProviderAcrossAppAndHostComposition()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DownKyi.Desktop", "App.axaml.cs"));
        var aboutSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "Settings",
            "ViewAboutViewModel.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Composition",
            "DesktopComposition.cs"));
        var hostSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "Composition",
            "DownKyiHost.cs"));

        Assert.Contains("new ApplicationLogProvider", appSource, StringComparison.Ordinal);
        Assert.Contains("using DownKyi.Infrastructure.Logging;", appSource, StringComparison.Ordinal);
        Assert.Contains("services.AddDownKyiDesktop(_loggerFactory, _logProvider)", appSource,
            StringComparison.Ordinal);
        var initializedMessage = appSource.IndexOf("Application initialized.", StringComparison.Ordinal);
        var initializationFlush = appSource.IndexOf(
            "_logProvider.FlushAsync(CancellationToken.None)",
            StringComparison.Ordinal);
        Assert.True(initializedMessage >= 0 && initializationFlush > initializedMessage);
        Assert.Contains("ObserveBackgroundTask(", appSource[initializedMessage..], StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", aboutSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(loggerFactory)", compositionSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(logService)", compositionSource, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddLogging()", hostSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchDownloadPreparationUsesOneHostOwnedFifoAbovePageLifetimes()
    {
        var ownerSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Services", "Media",
            "ContentDownloadBatchOwner.cs");
        var compositionSource = ReadSource(
            "src", "DownKyi.Desktop",
            "Composition",
            "DesktopComposition.cs");

        Assert.Contains("ContentDownloadCoordinator coordinator", ownerSource, StringComparison.Ordinal);
        Assert.Contains("Channel.CreateUnbounded<BatchRequest>", ownerSource, StringComparison.Ordinal);
        Assert.Contains("SingleReader = true", ownerSource, StringComparison.Ordinal);
        Assert.Contains("_applicationCancellation.CreateOperationScope", ownerSource, StringComparison.Ordinal);
        Assert.Contains("await workerTask.WaitAsync(cancellationToken)", ownerSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<ContentDownloadBatchOwner>()", compositionSource, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<ContentDownloadBatchOwner>()", compositionSource,
            StringComparison.Ordinal);

        foreach (var fileName in new[]
                 {
                     "ViewMyBangumiFollowViewModel.cs",
                     "ViewMyFavoritesViewModel.cs",
                     "ViewMyHistoryViewModel.cs",
                     "ViewMyToViewVideoViewModel.cs",
                     "ViewPublicationViewModel.cs",
                     "ViewPublicFavoritesViewModel.cs",
                     "ViewSeasonsSeriesDetailViewModel.cs"
                 })
        {
            var source = ReadSource(
                "src", "DownKyi.Desktop",
                "ViewModels",
                fileName);
            Assert.Contains("CancelDownloadPreparationCommand", source, StringComparison.Ordinal);
            Assert.Contains("ReleaseCancellationSource(ref _downloadCancellation", source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("CancelAndDispose(ref _downloadCancellation)", source,
                StringComparison.Ordinal);
        }
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

    private static string ReadSource(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));
    }
}
