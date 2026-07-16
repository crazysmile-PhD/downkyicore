using System.Net;
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
        using var handler = new StubHandler("{}");
        using var httpClient = CreateHttpClient(handler);
        var service = new VersionCheckerService(httpClient, "owner", "repo");
        var currentVersion = new AppInfo().VersionName;

        Assert.False(service.IsNewVersionAvailable($"v{currentVersion}"));
    }

    [Fact]
    public void IsNewVersionAvailableReturnsTrueForGreaterVersion()
    {
        using var handler = new StubHandler("{}");
        using var httpClient = CreateHttpClient(handler);
        var service = new VersionCheckerService(httpClient, "owner", "repo");

        Assert.True(service.IsNewVersionAvailable("v99.0.0"));
    }

    [Fact]
    public async Task LatestReleaseCheckPreservesPreCanceledRequest()
    {
        using var handler = new StubHandler("{}");
        using var httpClient = CreateHttpClient(handler);
        var service = new VersionCheckerService(httpClient, "owner", "repo");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetLatestReleaseAsync(false, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task PrereleaseCheckUsesReleaseCollectionEndpoint()
    {
        using var handler = new StubHandler("[{\"tag_name\":\"v2.0.0-beta.1\"}]");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
        var service = new VersionCheckerService(httpClient, "owner", "repo");

        var release = await service.GetLatestReleaseAsync(
            true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("v2.0.0-beta.1", release?.TagName);
        Assert.Equal("https://api.github.test/repos/owner/repo/releases", handler.RequestUri?.AbsoluteUri);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://api.github.test/")
        };
    }

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }
}
