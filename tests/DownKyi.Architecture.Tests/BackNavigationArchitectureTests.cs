namespace DownKyi.Architecture.Tests;

public sealed class BackNavigationArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("ViewDownloadManagerViewModel.cs")]
    [InlineData("ViewFriendsViewModel.cs")]
    [InlineData("ViewLoginViewModel.cs")]
    [InlineData("ViewMyBangumiFollowViewModel.cs")]
    [InlineData("ViewMyFavoritesViewModel.cs")]
    [InlineData("ViewMyHistoryViewModel.cs")]
    [InlineData("ViewMySpaceViewModel.cs")]
    [InlineData("ViewMyToViewVideoViewModel.cs")]
    [InlineData("ViewPublicationViewModel.cs")]
    [InlineData("ViewPublicFavoritesViewModel.cs")]
    [InlineData("ViewSeasonsSeriesViewModel.cs")]
    [InlineData("ViewSettingsViewModel.cs")]
    [InlineData("ViewToolboxViewModel.cs")]
    [InlineData("ViewUserSpaceViewModel.cs")]
    [InlineData("ViewVideoDetailViewModel.cs")]
    public void MainRegionBackCommandsUseHistoryBeforeParentFallback(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
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
