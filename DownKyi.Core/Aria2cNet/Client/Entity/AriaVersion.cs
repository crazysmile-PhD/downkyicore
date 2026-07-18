using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class AriaVersion
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("jsonrpc")] public string Jsonrpc { get; set; } = string.Empty;

    [JsonProperty("result")] public AriaVersionResult? Result { get; set; }

    [JsonProperty("error")] public AriaError? Error { get; set; }

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}

[JsonObject]
public class AriaVersionResult
{
    [JsonProperty("enabledFeatures")] public IReadOnlyList<string> EnabledFeatures { get; set; } = Array.Empty<string>();

    [JsonProperty("version")] public string Version { get; set; } = string.Empty;

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
