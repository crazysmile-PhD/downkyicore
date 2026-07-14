using System;
using System.Text.Json.Serialization;

namespace DownKyi.Models;

internal class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
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
