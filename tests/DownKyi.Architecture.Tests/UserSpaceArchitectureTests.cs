namespace DownKyi.Architecture.Tests;

public sealed class UserSpaceArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void UserSpaceViewModelProjectsCoordinatorResultsAndOwnsCancellation()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
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
    public void PublicFavoriteFoldersUseTypedRegionNavigationWithoutPrism()
    {
        var viewModel = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
            "ViewModels",
            "UserSpace",
            "ViewFavoritesViewModel.cs"));
        var view = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "DownKyi",
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
