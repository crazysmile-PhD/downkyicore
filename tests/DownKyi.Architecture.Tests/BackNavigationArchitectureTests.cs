namespace DownKyi.Architecture.Tests;

public sealed class BackNavigationArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("DownloadManagerViewModel.cs")]
    [InlineData("FriendsViewModel.cs")]
    [InlineData("LoginViewModel.cs")]
    [InlineData("MyBangumiFollowViewModel.cs")]
    [InlineData("MyFavoritesViewModel.cs")]
    [InlineData("MyHistoryViewModel.cs")]
    [InlineData("MySpaceViewModel.cs")]
    [InlineData("MyToViewVideoViewModel.cs")]
    [InlineData("PublicationViewModel.cs")]
    [InlineData("PublicFavoritesViewModel.cs")]
    [InlineData("SeasonsSeriesDetailViewModel.cs")]
    [InlineData("SettingsViewModel.cs")]
    [InlineData("ToolboxViewModel.cs")]
    [InlineData("UserSpaceViewModel.cs")]
    [InlineData("VideoDetailViewModel.cs")]
    public void MainRegionBackCommandsUseHistoryBeforeParentFallback(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            fileName));

        Assert.Contains("if (TryNavigateBack())", source, StringComparison.Ordinal);
        Assert.Contains("NavigateToParent(", source, StringComparison.Ordinal);
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
