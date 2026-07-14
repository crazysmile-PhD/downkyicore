using Newtonsoft.Json;

namespace DownKyi.Core.Aria2cNet.Client.Entity;

[JsonObject]
public class SystemListMethods
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;

    [JsonProperty("jsonrpc")] public string Jsonrpc { get; set; } = string.Empty;

    [JsonProperty("result")] public IReadOnlyList<string> Result { get; set; } = Array.Empty<string>();

    [JsonProperty("error")] public AriaError Error { get; set; } = new();

    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}
