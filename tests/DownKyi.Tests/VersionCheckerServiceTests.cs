using System.Text.Json;
using DownKyi.Models;
using DownKyi.Services;

namespace DownKyi.Tests;

public class VersionCheckerServiceTests
{
    [Fact]
    public void GitHubReleaseJsonUsesSourceGeneratedContract()
    {
        const string json = """
            {
              "tag_name": "v1.2.3",
              "name": "Release",
              "body": "Notes",
              "prerelease": true,
              "published_at": "2026-07-10T00:00:00Z"
            }
            """;

        var release = JsonSerializer.Deserialize(json, GitHubJsonContext.Default.GitHubRelease);

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal("Release", release.Name);
        Assert.Equal("Notes", release.Body);
        Assert.True(release.Prerelease);
        Assert.Equal(new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc), release.PublishedAt);
    }

    [Theory]
    [InlineData("v1.0.32", "1.0.32")]
    [InlineData("1.0.32-debug", "1.0.32")]
    [InlineData("1.0.32+abcdef", "1.0.32")]
    [InlineData("v1.0.32-beta.1", "1.0.32")]
    public void NormalizeVersionNameReturnsComparableSemverCore(string input, string expected)
    {
        Assert.Equal(expected, AppInfo.NormalizeVersionName(input));
    }

    [Theory]
    [InlineData("v1.0.32", 10032)]
    [InlineData("1.2.3-debug", 10203)]
    [InlineData("not-a-version", 0)]
    public void VersionNameToCodeUsesNormalizedVersion(string input, int expected)
    {
        Assert.Equal(expected, AppInfo.VersionNameToCode(input));
    }

    [Fact]
    public void IsNewVersionAvailableReturnsFalseForCurrentVersion()
    {
        var service = new VersionCheckerService("owner", "repo");
        var currentVersion = new AppInfo().VersionName;

        Assert.False(service.IsNewVersionAvailable($"v{currentVersion}"));
    }

    [Fact]
    public void IsNewVersionAvailableReturnsTrueForGreaterVersion()
    {
        var service = new VersionCheckerService("owner", "repo");

        Assert.True(service.IsNewVersionAvailable("v99.0.0"));
    }
}
