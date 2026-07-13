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
        Assert.Contains("UserSpaceLoadCoordinator.LoadAsync", source, StringComparison.Ordinal);
        Assert.Contains("OnNavigatedFrom", source, StringComparison.Ordinal);
        Assert.Contains("_loadCancellation?.Cancel()", source, StringComparison.Ordinal);
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
