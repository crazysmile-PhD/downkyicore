using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.VideoStream.Models;

public class PlayUrlDashDolby : BaseModel
{
    private IReadOnlyList<PlayUrlDashVideo> _audio = Array.Empty<PlayUrlDashVideo>();
    // type
    [JsonProperty("audio")]
    public IReadOnlyList<PlayUrlDashVideo> Audio
    {
        get => _audio;
        set => _audio = value ?? Array.Empty<PlayUrlDashVideo>();
    }
}
