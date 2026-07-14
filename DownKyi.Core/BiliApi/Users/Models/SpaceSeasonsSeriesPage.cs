using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Users.Models;

public class SpaceSeasonsSeriesPage : BaseModel
{
    [JsonProperty("page_num")] public int PageNum { get; set; }
    [JsonProperty("page_size")] public int PageSize { get; set; }
    [JsonProperty("total")] public int Total { get; set; }
}
