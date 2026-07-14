namespace DownKyi.Architecture.Tests;

public sealed class AppLifecycleArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ExitPathDoesNotSynchronouslyWaitForAsyncCleanup()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));

        Assert.DoesNotContain("OnExitAsync().Wait", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult()", source, StringComparison.Ordinal);
        Assert.Contains("RequestShutdownAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowCompletesCleanupBeforeConfirmingClose()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "Views",
            "MainWindow.axaml.cs"));

        Assert.Contains("await app.RequestShutdownAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_closeConfirmed = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRestartPromptsCannotBypassAsynchronousCleanup()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "Settings",
            "ViewNetworkViewModel.cs"));

        Assert.DoesNotContain("IClassicDesktopStyleApplicationLifetime", source, StringComparison.Ordinal);
        Assert.Contains("await App.Current.RequestShutdownAsync()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDoesNotOwnGlobalDownloadCollections()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));

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
        Assert.Contains("host.StopAsync(CancellationToken.None)", appSource, StringComparison.Ordinal);
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
