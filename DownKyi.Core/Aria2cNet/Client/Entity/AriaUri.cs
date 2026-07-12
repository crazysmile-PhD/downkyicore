using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class AriaUri
{
    [JsonProperty("status")] public string Status { get; set; } = string.Empty;

    [JsonProperty("uri")] public string Address { get; set; } = string.Empty;

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
