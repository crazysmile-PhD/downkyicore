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
