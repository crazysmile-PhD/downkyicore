using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class RequestPreparationBenchmarks
{
    private const string SampleJson = """
        {
          "code": 0,
          "message": "0",
          "data": {
            "bvid": "BV1xx411c7mD",
            "cid": 171776208,
            "title": "benchmark sample"
          }
        }
        """;

    private readonly Dictionary<string, object?> _parameters = new()
    {
        ["bvid"] = "BV1xx411c7mD",
        ["cid"] = 171776208,
        ["qn"] = 120,
        ["fnval"] = 4048,
        ["fourk"] = 1
    };
    private readonly JsonSerializerOptions _jsonOptions = new();

    [Benchmark]
    public string BuildRequestAddress()
    {
        return BiliWebClient.BuildRequestUrlForTests(
            "https://api.bilibili.com/x/player/wbi/playurl?platform=html5",
            "GET",
            _parameters);
    }

    [Benchmark]
    public JsonElement DeserializeApiEnvelope()
    {
        return JsonSerializer.Deserialize<JsonElement>(SampleJson, _jsonOptions);
    }
}
