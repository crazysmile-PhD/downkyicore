using System.Text.RegularExpressions;

namespace DownKyi.Architecture.Tests;

public sealed class InputParsingArchitectureTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ParserRoot = Path.Combine(
        RepositoryRoot,
        "DownKyi.Core",
        "BiliApi",
        "BiliUtils");
    private static readonly Regex PublicMethodRegex = new(
        @"public static (?:bool|long|string) ([A-Za-z_][A-Za-z0-9_]*)\(",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    [Fact]
    public void ParseEntranceOwnersRemainFocusedAndBelowTheOwnerBudget()
    {
        var files = Directory
            .EnumerateFiles(ParserRoot, "ParseEntrance*.cs", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(8, files.Length);
        Assert.All(files, path =>
            Assert.True(
                File.ReadAllLines(path).Length <= 120,
                $"{Path.GetFileName(path)} exceeded the 120-line input-owner budget."));
    }

    [Fact]
    public void PublicParserFamiliesRemainInTheirDedicatedOwners()
    {
        AssertPublicMethods(
            "ParseEntrance.Video.cs",
            "IsAvId",
            "IsAvUrl",
            "GetAvId",
            "IsBvId",
            "IsBvUrl",
            "GetBvId");
        AssertPublicMethods(
            "ParseEntrance.Bangumi.cs",
            "IsBangumiSeasonId",
            "IsBangumiSeasonUrl",
            "GetBangumiSeasonId",
            "IsBangumiEpisodeId",
            "IsBangumiEpisodeUrl",
            "GetBangumiEpisodeId",
            "IsBangumiMediaId",
            "IsBangumiMediaUrl",
            "GetBangumiMediaId");
        AssertPublicMethods(
            "ParseEntrance.Cheese.cs",
            "IsCheeseSeasonUrl",
            "GetCheeseSeasonId",
            "IsCheeseEpisodeUrl",
            "GetCheeseEpisodeId");
        AssertPublicMethods(
            "ParseEntrance.Favorites.cs",
            "IsFavoritesId",
            "IsFavoritesUrl",
            "GetFavoritesId");
        AssertPublicMethods(
            "ParseEntrance.UserSpace.cs",
            "IsUserId",
            "IsUserUrl",
            "GetUserId");
        AssertPublicMethods(
            "ParseEntrance.UserVideoList.cs",
            "IsUserVideoListUrl",
            "GetUserVideoListId");
    }

    [Fact]
    public void SharedUriOwnerExposesNoPublicParsingSurface()
    {
        var source = Read("ParseEntrance.Uri.cs");

        Assert.Empty(PublicMethodRegex.Matches(source));
        Assert.Contains("private static string GetId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSpaceParsingRequiresAnExactHostAndNumericPath()
    {
        var source = Read("ParseEntrance.UserSpace.cs");

        Assert.Contains("Uri.TryCreate", source, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(uri.Host, \"space.bilibili.com\", StringComparison.OrdinalIgnoreCase)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("segments.Length == 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Contains(\"space.bilibili.com\"", source, StringComparison.Ordinal);
    }

    private static void AssertPublicMethods(string fileName, params string[] expected)
    {
        var actual = PublicMethodRegex
            .Matches(Read(fileName))
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    private static string Read(string fileName)
    {
        return File.ReadAllText(Path.Combine(ParserRoot, fileName));
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
