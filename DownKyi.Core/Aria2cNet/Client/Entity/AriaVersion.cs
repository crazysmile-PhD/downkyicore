using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class AriaVersion
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("jsonrpc")] public string Jsonrpc { get; set; } = string.Empty;

    [JsonProperty("result")] public AriaVersionResult Result { get; set; } = new();

    [JsonProperty("error")] public AriaError Error { get; set; } = new();

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}

[JsonObject]
public class AriaVersionResult
{
    [JsonProperty("enabledFeatures")] public List<string> EnabledFeatures { get; set; } = new();

    [JsonProperty("version")] public string Version { get; set; } = string.Empty;

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
