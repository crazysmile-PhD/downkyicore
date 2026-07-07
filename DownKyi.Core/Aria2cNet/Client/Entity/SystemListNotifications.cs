using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class SystemListNotifications
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("jsonrpc")] public string Jsonrpc { get; set; } = string.Empty;

    [JsonProperty("result")] public List<string> Result { get; set; } = new();

    [JsonProperty("error")] public AriaError Error { get; set; } = new();

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
