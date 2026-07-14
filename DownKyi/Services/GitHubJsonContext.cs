using System.Text.Json.Serialization;
using DownKyi.Models;

namespace DownKyi.Services;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubRelease[]))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
