using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlDurl : BaseModel
{
    [JsonProperty("order")] public int Order { get; set; }
    [JsonProperty("length")] public long Length { get; set; }

    [JsonProperty("size")] public long Size { get; set; }

    // ahead
    // vhead
    [JsonProperty("url")] public string SourceAddress { get; set; } = string.Empty;
    [JsonProperty("backup_url")] public IReadOnlyList<string> BackupUrl { get; set; } = Array.Empty<string>();
}
