namespace DownKyi.Architecture.Tests;

public sealed class UiThemeArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopUsesOneFluentThemeAndCentralDesignTokens()
    {
        var appSource = ReadSource("DownKyi", "App.axaml");
        var themeSource = ReadSource("DownKyi", "Themes", "ThemeDefault.axaml");
        var tokenSource = ReadSource("DownKyi", "Themes", "DesignTokens.axaml");
        var projectSource = ReadSource("DownKyi", "DownKyi.csproj");

        Assert.Contains("<FluentTheme />", appSource, StringComparison.Ordinal);
        Assert.Contains("Themes/Fluent.xaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SimpleTheme", appSource, StringComparison.Ordinal);
        Assert.Contains("/Themes/DesignTokens.axaml", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("/Themes/DesignTokens.axaml", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Default\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Dark\"", themeSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiFontSizeBody", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiSpacingMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiRadiusMedium", tokenSource, StringComparison.Ordinal);
        Assert.Contains("DownKyiElevationLow", tokenSource, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Themes.Fluent", projectSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Themes.Simple", projectSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FluentThemeDependencyStaysInsideDesktopProject()
    {
        var consumers = Directory
            .EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("Avalonia.Themes.Fluent", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(["DownKyi/DownKyi.csproj"], consumers);
    }

    [Theory]
    [InlineData("DownKyi", "Views", "DownloadManager", "ViewDownloading.axaml")]
    [InlineData("DownKyi", "Views", "DownloadManager", "ViewDownloadFinished.axaml")]
    [InlineData("DownKyi", "Views", "ViewMyHistory.axaml")]
    [InlineData("DownKyi", "Views", "ViewPublicFavorites.axaml")]
    public void LargeListsKeepVirtualizingPanels(params string[] pathParts)
    {
        Assert.Contains("VirtualizingStackPanel", ReadSource(pathParts), StringComparison.Ordinal);
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
