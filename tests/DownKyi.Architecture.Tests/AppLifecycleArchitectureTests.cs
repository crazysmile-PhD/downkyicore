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
        Assert.Contains("DownloadListState", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostOwnsDownloadBootstrapAndRuntimeLifecycle()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot, "DownKyi", "App.axaml.cs"));

        Assert.Contains("DownloadBootstrapHostedService", source, StringComparison.Ordinal);
        Assert.Contains("host.StopAsync(CancellationToken.None)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadDownloadStateAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingHistoryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRemainingDownloadHistoryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateDownloadService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadingAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDownloadedAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_downloadService", source, StringComparison.Ordinal);
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
