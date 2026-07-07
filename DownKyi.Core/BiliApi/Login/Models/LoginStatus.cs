using DownKyi.Core.BiliApi.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.BiliApi.Login.Models
{
    [JsonObject]
    public class LoginStatus : BaseModel
    {
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = string.Empty;

        [JsonProperty("data")] public LoginStatusData Data { get; set; } = new();
    }

    [JsonObject]
    public class LoginStatusData : BaseModel
    {
        [JsonProperty("url")] public string Url { get; set; } = string.Empty;
        [JsonProperty("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonProperty("code")] public int Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; } = string.Empty;
    }
}
