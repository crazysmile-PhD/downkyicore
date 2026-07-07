using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class SystemMulticallMathod
{
    [JsonProperty("method")] public string Method { get; set; } = string.Empty;

    [JsonProperty("params")] public List<object> Params { get; set; } = new();
}
