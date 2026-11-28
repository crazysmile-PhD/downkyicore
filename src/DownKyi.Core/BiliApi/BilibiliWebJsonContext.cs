using System.Text.Json.Serialization;

namespace DownKyi.Core.BiliApi;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(BilibiliResponseMetadata))]
internal sealed partial class BilibiliWebJsonContext : JsonSerializerContext;

internal sealed class BilibiliResponseMetadata
{
    [JsonPropertyName("code")]
    public int? Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
