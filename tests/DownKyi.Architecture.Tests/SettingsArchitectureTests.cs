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
    [InlineData("DownKyi", "ViewModels", "Settings", "ViewNetworkViewModel.cs")]
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

        Assert.Contains("MainWindow(MainWindowViewModel viewModel, ISettingsStore settingsStore)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public MainWindow()", source, StringComparison.Ordinal);
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
