using System;
using Newtonsoft.Json;

namespace DownKyi.Models;

public class GitHubRelease
{
    [JsonProperty("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("body")]
    public string Body { get; set; } = string.Empty;

    [JsonProperty("prerelease")]
    public bool Prerelease { get; set; }

    [JsonProperty("published_at")]
    public DateTime? PublishedAt { get; set; }

    public bool IsEmpty()
    {
        return string.IsNullOrEmpty(TagName) &&
               string.IsNullOrEmpty(Name) &&
               string.IsNullOrEmpty(Body) &&
               PublishedAt == null;
    }

    public static bool IsNullOrEmpty(GitHubRelease? release)
    {
        return release == null || release.IsEmpty();
    }
}
