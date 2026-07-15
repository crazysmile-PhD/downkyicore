namespace DownKyi.Architecture.Tests;

public sealed class AppLifecycleArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ExitPathDoesNotSynchronouslyWaitForAsyncCleanup()
    {
        var appSource = ReadSource("DownKyi", "App.axaml.cs");
        var lifecycleSource = ReadSource(
            "DownKyi",
            "Platform",
            "AvaloniaApplicationLifecycle.cs");

        Assert.DoesNotContain(".GetAwaiter().GetResult()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", appSource, StringComparison.Ordinal);
        Assert.Contains("IApplicationLifecycle", appSource, StringComparison.Ordinal);
        Assert.Contains("host.StopAsync(CancellationToken.None)", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("_settingsStore.FlushAsync", lifecycleSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowCompletesCleanupBeforeConfirmingClose()
    {
        var source = ReadSource("DownKyi", "Views", "MainWindow.axaml.cs");

        Assert.Contains("await _applicationLifecycle.RequestShutdownAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_closeConfirmed = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRestartPromptsCannotBypassAsynchronousCleanup()
    {
        var source = ReadSource(
            "DownKyi",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.cs");

        Assert.DoesNotContain("IClassicDesktopStyleApplicationLifetime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.Contains("_applicationLifecycle.RestartAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewModelsCannotUseStaticApplicationOrProcessLifecycle()
    {
        var files = Directory.GetFiles(
            Path.Combine(RepositoryRoot, "DownKyi", "ViewModels"),
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
        var appSource = ReadSource("DownKyi", "App.axaml.cs");
        var lifecycleSource = ReadSource(
            "DownKyi",
            "Platform",
            "AvaloniaApplicationLifecycle.cs");
        var restartSource = ReadSource(
            "DownKyi",
            "Platform",
            "ProcessRestartLauncher.cs");

        Assert.Contains("SingleInstanceGuard.TryAcquire", appSource, StringComparison.Ordinal);
        Assert.Contains("_applicationLifecycle.AttachHost", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new Mutex", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StopHostAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FlushAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("IProcessRestartLauncher", lifecycleSource, StringComparison.Ordinal);
        Assert.Contains("WaitForExitAsync", restartSource, StringComparison.Ordinal);
        Assert.Contains("ArgumentList.Add", restartSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDoesNotOwnGlobalDownloadCollections()
    {
        var source = ReadSource("DownKyi", "App.axaml.cs");

        Assert.DoesNotContain("static ImmutableObservableCollection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadingList { get;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadedList { get;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DownloadListState", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostOwnsDownloadBootstrapAndRuntimeLifecycle()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "LegacyDesktopComposition.cs"));

        Assert.Contains("CreateLegacyDesktopHost", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("host.StopAsync", appSource, StringComparison.Ordinal);
        Assert.Contains("DownloadBootstrapHostedService", compositionSource, StringComparison.Ordinal);
        Assert.Contains("IDownloadRuntimeFactory", compositionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadDownloadStateAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingHistoryAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingDownloadHistoryAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDownloadService", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadingAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadedAsync", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadService", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDelegatesPrismRegistrationToCompatibilityComposition()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var compositionSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "LegacyPrismComposition.cs"));

        Assert.Contains("RegisterLegacyApplication", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterForNavigation", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterDialog", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterSingleton", appSource, StringComparison.Ordinal);
        Assert.Contains("RegisterForNavigation", compositionSource, StringComparison.Ordinal);
        Assert.Contains("RegisterDialog", compositionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationLoggingUsesOneProviderAcrossPrismAndHostComposition()
    {
        var appSource = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));
        var aboutSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "Settings",
            "ViewAboutViewModel.cs"));
        var legacyHostSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Composition",
            "LegacyDesktopComposition.cs"));
        var hostSource = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "DownKyi.Desktop",
            "Composition",
            "DownKyiHost.cs"));

        Assert.Contains("new ApplicationLogProvider", appSource, StringComparison.Ordinal);
        Assert.Contains("RegisterInstance<IApplicationLogService>", appSource, StringComparison.Ordinal);
        Assert.Contains("RegisterInstance<ILoggerFactory>", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LogManager.", aboutSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(loggerFactory)", legacyHostSource, StringComparison.Ordinal);
        Assert.Contains("services.AddSingleton(logService)", legacyHostSource, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddLogging()", hostSource, StringComparison.Ordinal);
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
