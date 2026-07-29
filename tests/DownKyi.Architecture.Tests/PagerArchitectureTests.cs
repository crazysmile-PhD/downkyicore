namespace DownKyi.Architecture.Tests;

public sealed class PagerArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string PagerRoot = Path.Combine(
        RepositoryRoot,
        "src",
        "DownKyi.Desktop",
        "CustomControl");

    [Fact]
    public void PagerOwnersRemainFocusedAndBelowBudget()
    {
        var ownerNames = new[]
        {
            "CustomPagerViewModel.cs",
            "CustomPagerViewModel.State.cs",
            "CustomPagerViewModel.Commands.cs",
            "PagerLayout.cs"
        };

        Assert.All(ownerNames, name =>
        {
            var path = Path.Combine(PagerRoot, name);
            Assert.True(File.Exists(path), $"{name} is missing.");
            Assert.True(
                File.ReadAllLines(path).Length <= 150,
                $"{name} exceeded the 150-line pager-owner budget.");
        });
    }

    [Fact]
    public void ParameterlessPagerButtonsUseParameterlessCommands()
    {
        var commandSource = Read("CustomPagerViewModel.Commands.cs");
        var xamlSource = Read("CustomPager.axaml");

        Assert.Contains("RelayCommand PreviousCommand", commandSource, StringComparison.Ordinal);
        Assert.Contains("RelayCommand FirstCommand", commandSource, StringComparison.Ordinal);
        Assert.Contains("RelayCommand NextCommand", commandSource, StringComparison.Ordinal);
        Assert.Contains("RelayCommand LastCommand", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RelayCommand<object> PreviousCommand", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RelayCommand<object> NextCommand", commandSource, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PreviousCommand}\"", xamlSource, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NextCommand}\"", xamlSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PagerLayoutRemainsPureAndFrameworkFree()
    {
        var source = Read("PagerLayout.cs");

        Assert.Contains("record struct PagerLayout", source, StringComparison.Ordinal);
        Assert.Contains("PagerLayout Create", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommunityToolkit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ICommand", source, StringComparison.Ordinal);
    }

    private static string Read(string fileName)
    {
        return File.ReadAllText(Path.Combine(PagerRoot, fileName));
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

        throw new DirectoryNotFoundException("Could not locate the DownKyi repository root.");
    }
}
