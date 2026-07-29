namespace DownKyi.Architecture.Tests;

public sealed class UserSpaceArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void UserSpaceViewModelProjectsCoordinatorResultsAndOwnsCancellation()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "ViewUserSpaceViewModel.cs"));

        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.Contains("IUserSpaceLoadCoordinator", source, StringComparison.Ordinal);
        Assert.Contains(".LoadAsync(mid, cancellationToken)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IWbiKeyProvider", source, StringComparison.Ordinal);
        Assert.Contains("if (_loadedMid == parameter)", source, StringComparison.Ordinal);
        Assert.Contains("OnNavigatedFrom", source, StringComparison.Ordinal);
        Assert.Contains("_loadCancellation?.Cancel()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSpaceBindingStateRemainsServiceFreeAndBelowTheOwnerBudget()
    {
        var viewModelDirectory = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels");
        var workflowPath = Path.Combine(viewModelDirectory, "ViewUserSpaceViewModel.cs");
        var statePath = Path.Combine(viewModelDirectory, "ViewUserSpaceViewModel.State.cs");
        var workflowSource = File.ReadAllText(workflowPath);
        var stateSource = File.ReadAllText(statePath);

        Assert.True(
            File.ReadLines(workflowPath).Count() <= 450,
            "User-space workflow owner exceeded its size budget.");
        Assert.True(
            File.ReadLines(statePath).Count() <= 200,
            "User-space binding-state owner exceeded its size budget.");
        Assert.Contains("IUserSpaceLoadCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", workflowSource, StringComparison.Ordinal);
        Assert.Contains("AppNavigationContext", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<ViewUserSpaceViewModel>", workflowSource, StringComparison.Ordinal);

        foreach (var property in new[]
        {
            "ArrowBack",
            "Loading",
            "NoDataVisibility",
            "LoadingVisibility",
            "ViewVisibility",
            "ContentVisibility",
            "TopNavigationBg",
            "Background",
            "Header",
            "UserName",
            "Sex",
            "Level",
            "VipTypeVisibility",
            "VipType",
            "Sign",
            "IsFollowed",
            "TabLeftBanners",
            "TabRightBanners",
            "SelectedRightBanner"
        })
        {
            Assert.Contains($" {property}", stateSource, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[]
        {
            "IUserSpaceLoadCoordinator",
            "ISettingsStore",
            "CancellationToken",
            "AppNavigation",
            "ILogger",
            "LoadAsync(",
            "Navigate("
        })
        {
            Assert.DoesNotContain(forbidden, stateSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BangumiFollowBindingStateRemainsServiceFreeAndBelowTheOwnerBudget()
    {
        var viewModelDirectory = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels");
        var workflowPath = Path.Combine(viewModelDirectory, "ViewMyBangumiFollowViewModel.cs");
        var statePath = Path.Combine(viewModelDirectory, "ViewMyBangumiFollowViewModel.State.cs");
        var workflowSource = File.ReadAllText(workflowPath);
        var stateSource = File.ReadAllText(statePath);

        Assert.True(
            File.ReadLines(workflowPath).Count() <= 450,
            "Bangumi-follow workflow owner exceeded its size budget.");
        Assert.True(
            File.ReadLines(statePath).Count() <= 150,
            "Bangumi-follow binding-state owner exceeded its size budget.");
        Assert.Contains("IUserSpacePageCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("IContentDownloadCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", workflowSource, StringComparison.Ordinal);
        Assert.Contains("AppNavigationContext", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CustomPagerViewModel", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<ViewMyBangumiFollowViewModel>", workflowSource, StringComparison.Ordinal);

        foreach (var property in new[]
        {
            "PageName",
            "ArrowBack",
            "DownloadManage",
            "TabHeaders",
            "SelectTabId",
            "IsEnabled",
            "ContentVisibility",
            "Medias",
            "IsSelectAll",
            "Loading",
            "LoadingVisibility",
            "NoDataVisibility"
        })
        {
            Assert.Contains($" {property}", stateSource, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[]
        {
            "IUserSpacePageCoordinator",
            "IContentDownloadCoordinator",
            "CancellationToken",
            "AppNavigation",
            "CustomPagerViewModel",
            "ILogger",
            "LoadBangumiFollowPageAsync(",
            "AddAsync(",
            "Navigate("
        })
        {
            Assert.DoesNotContain(forbidden, stateSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublicFavoriteFoldersUseTypedRegionNavigationWithoutPrism()
    {
        var viewModel = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels",
            "UserSpace",
            "ViewFavoritesViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "Views",
            "UserSpace",
            "ViewFavorites.axaml"));

        Assert.Contains("AppRoute.PublicFavorites", viewModel, StringComparison.Ordinal);
        Assert.Contains("AppRoute.UserSpace", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Prism", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prism:", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ViewModelLocator", view, StringComparison.OrdinalIgnoreCase);
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
