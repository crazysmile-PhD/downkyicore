using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Models;

namespace DownKyi.Services
{
    internal class VersionCheckerService
    {
        private readonly string _repoOwner;
        private readonly string _repoName;
        private readonly bool _includePrereleases;
        private readonly string _currentVersion;

        public VersionCheckerService(string repoOwner, string repoName, bool includePrereleases = false)
        {
            _repoOwner = repoOwner;
            _repoName = repoName;
            _includePrereleases = includePrereleases;
            _currentVersion = AppInfo.NormalizeVersionName(new AppInfo().VersionName);
        }


        public async Task<GitHubRelease?> GetLatestReleaseAsync(
            string? excludedVersion = null,
            CancellationToken cancellationToken = default)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "downkyi");
            client.Timeout = TimeSpan.FromSeconds(3);
            if (_includePrereleases)
            {
                var releasesUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases";
                var releasesJson = await client
                    .GetStringAsync(new Uri(releasesUrl), cancellationToken)
                    .ConfigureAwait(true);
                var releases = JsonSerializer.Deserialize(releasesJson, GitHubJsonContext.Default.GitHubReleaseArray);

                return string.IsNullOrEmpty(excludedVersion)
                    ? releases?.FirstOrDefault()
                    : releases?.FirstOrDefault(r => r.TagName.TrimStart('v') != excludedVersion);
            }

            var latestReleaseUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
            var latestReleaseJson = await client
                .GetStringAsync(new Uri(latestReleaseUrl), cancellationToken)
                .ConfigureAwait(true);
            var release = JsonSerializer.Deserialize(latestReleaseJson, GitHubJsonContext.Default.GitHubRelease);

            return string.IsNullOrEmpty(excludedVersion) ||
                   release?.TagName.TrimStart('v') != excludedVersion ? release : null;
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
}
