using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Models;

namespace DownKyi.Services;

internal sealed class VersionCheckerService
{
    private readonly HttpClient _httpClient;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly string _currentVersion;

    public VersionCheckerService(HttpClient httpClient)
        : this(httpClient, AppConstant.RepoOwner, AppConstant.RepoName)
    {
    }

    internal VersionCheckerService(HttpClient httpClient, string repoOwner, string repoName)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _repoOwner = repoOwner ?? throw new ArgumentNullException(nameof(repoOwner));
        _repoName = repoName ?? throw new ArgumentNullException(nameof(repoName));
        _currentVersion = AppInfo.NormalizeVersionName(new AppInfo().VersionName);
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(
        bool includePrereleases,
        string? excludedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = includePrereleases
            ? $"repos/{_repoOwner}/{_repoName}/releases"
            : $"repos/{_repoOwner}/{_repoName}/releases/latest";
        var responseJson = await _httpClient
            .GetStringAsync(new Uri(endpoint, UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        if (includePrereleases)
        {
            var releases = JsonSerializer.Deserialize(
                responseJson,
                GitHubJsonContext.Default.GitHubReleaseArray);
            return string.IsNullOrEmpty(excludedVersion)
                ? releases?.FirstOrDefault()
                : releases?.FirstOrDefault(release =>
                    release.TagName.TrimStart('v') != excludedVersion);
        }

        var latestRelease = JsonSerializer.Deserialize(
            responseJson,
            GitHubJsonContext.Default.GitHubRelease);
        return string.IsNullOrEmpty(excludedVersion) ||
               latestRelease?.TagName.TrimStart('v') != excludedVersion
            ? latestRelease
            : null;
    }

    public bool IsNewVersionAvailable(string latestVersion)
    {
        var latestReleaseVersion = AppInfo.NormalizeVersionName(latestVersion);
        if (!Version.TryParse(_currentVersion, out var current) ||
            !Version.TryParse(latestReleaseVersion, out var latest))
        {
            return false;
        }

        return latest > current;
    }
}
