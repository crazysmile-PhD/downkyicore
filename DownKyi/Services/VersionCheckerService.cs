using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.Models;

namespace DownKyi.Services
{
    public class VersionCheckerService
    {
        private readonly string _repoOwner;
        private readonly string _repoName;
        private readonly bool _includePrereleases;

        public VersionCheckerService(string repoOwner, string repoName, bool includePrereleases = false)
        {
            _repoOwner = repoOwner;
            _repoName = repoName;
            _includePrereleases = includePrereleases;
        }


        public async Task<GitHubRelease?> GetLatestReleaseAsync(string? excludedVersion = null)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "downkyi");
            client.Timeout = TimeSpan.FromSeconds(3);
            try
            {
                if (_includePrereleases)
                {
                    var releasesUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases";
                    var releasesJson = await client.GetStringAsync(releasesUrl);
                    var releases = JsonSerializer.Deserialize(releasesJson, GitHubJsonContext.Default.GitHubReleaseArray);

                    return string.IsNullOrEmpty(excludedVersion)
                        ? releases?.FirstOrDefault()
                        : releases?.FirstOrDefault(r => r.TagName.TrimStart('v') != excludedVersion);
                }
                else
                {
                    var latestReleaseUrl = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
                    var latestReleaseJson = await client.GetStringAsync(latestReleaseUrl);
                    var release = JsonSerializer.Deserialize(latestReleaseJson, GitHubJsonContext.Default.GitHubRelease);

                    return string.IsNullOrEmpty(excludedVersion) ||
                           release?.TagName.TrimStart('v') != excludedVersion ? release : null;
                }
            }
            catch (Exception e)
            {
                LogManager.Error(nameof(VersionCheckerService), e);
            }

            return null;
        }




        public bool IsNewVersionAvailable(string latestVersion)
        {
            var currentVersion = AppInfo.NormalizeVersionName(new AppInfo().VersionName);
            var latestReleaseVersion = AppInfo.NormalizeVersionName(latestVersion);
            if (!Version.TryParse(currentVersion, out var current) ||
                !Version.TryParse(latestReleaseVersion, out var latest))
            {
                return false;
            }

            return latest > current;
        }

    }
}
