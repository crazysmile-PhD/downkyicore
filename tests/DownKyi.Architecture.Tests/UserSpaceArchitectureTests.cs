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
            "UserSpaceViewModel.cs"));

        Assert.DoesNotContain("Task.Run", source, StringComparison.Ordinal);
        Assert.Contains("IUserSpaceLoadCoordinator", source, StringComparison.Ordinal);
        Assert.Contains(".LoadProfileAsync(requestedMid, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains(".LoadOverviewAsync(requestedMid, cancellationToken)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.SeasonsSeries", source, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.FavoriteFolders", source, StringComparison.Ordinal);
        Assert.Contains("[\"lifetime\"] = lifetime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IWbiKeyProvider", source, StringComparison.Ordinal);
        Assert.Contains("if (_loadedMid == parameter)", source, StringComparison.Ordinal);
        Assert.Contains("OnNavigatedFrom", source, StringComparison.Ordinal);
        Assert.Contains("_loadCancellation?.Cancel()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSpaceBindingStateRemainsServiceFree()
    {
        var viewModelDirectory = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels");
        var workflowPath = Path.Combine(viewModelDirectory, "UserSpaceViewModel.cs");
        var statePath = Path.Combine(viewModelDirectory, "UserSpaceViewModel.State.cs");
        var workflowSource = File.ReadAllText(workflowPath);
        var stateSource = File.ReadAllText(statePath);

        Assert.Contains("IUserSpaceLoadCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ISettingsStore", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", workflowSource, StringComparison.Ordinal);
        Assert.Contains("AppNavigationContext", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<UserSpaceViewModel>", workflowSource, StringComparison.Ordinal);

        foreach (var property in new[]
        {
            "ArrowBack",
            "Loading",
            "NoDataVisibility",
            "LoadingVisibility",
            "ViewVisibility",
            "ContentVisibility",
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
            "TopNavigationBg",
            "LoadAsync(",
            "Navigate("
        })
        {
            Assert.DoesNotContain(forbidden, stateSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BangumiFollowBindingStateRemainsServiceFree()
    {
        var viewModelDirectory = Path.Combine(
            RepositoryRoot,
            "src", "DownKyi.Desktop",
            "ViewModels");
        var workflowPath = Path.Combine(viewModelDirectory, "MyBangumiFollowViewModel.cs");
        var statePath = Path.Combine(viewModelDirectory, "MyBangumiFollowViewModel.State.cs");
        var workflowSource = File.ReadAllText(workflowPath);
        var stateSource = File.ReadAllText(statePath);

        Assert.Contains("IUserSpacePageCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("IContentDownloadCoordinator", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource", workflowSource, StringComparison.Ordinal);
        Assert.Contains("AppNavigationContext", workflowSource, StringComparison.Ordinal);
        Assert.Contains("CustomPagerViewModel", workflowSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<MyBangumiFollowViewModel>", workflowSource, StringComparison.Ordinal);

        foreach (var property in new[]
        {
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
            "FavoritesViewModel.cs"));
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
