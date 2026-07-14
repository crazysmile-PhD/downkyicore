using System.Net;
using DownKyi.Core.BiliApi;
using Newtonsoft.Json.Linq;
using BiliWebClient = DownKyi.Core.BiliApi.WebClient;

namespace DownKyi.Core.Tests;

public sealed class BiliApiContractSampleTests : IDisposable
{
    private static readonly string SampleDirectory = Path.Combine(
        FindRepositoryRoot(),
        "tests",
        "DownKyi.Core.Tests",
        "BiliApi",
        "JsonSamples");

    public BiliApiContractSampleTests()
    {
        BiliWebClient.SetBuvidForTests();
    }

    [Fact]
    public void SuccessSampleDeserializes()
    {
        ConfigureResponse("success.json");

        var result = RequestSample();

        Assert.Equal(0, result.Value<int>("code"));
        Assert.Equal(1, result["data"]?.Value<int>("id"));
    }

    [Fact]
    public void MissingDataSampleIsVisibleWithoutNullReference()
    {
        ConfigureResponse("missing-data.json");

        var result = RequestSample();

        Assert.Equal(0, result.Value<int>("code"));
        Assert.Null(result["data"]);
    }

    [Fact]
    public void RejectedCodeThrowsTypedApiFailure()
    {
        ConfigureResponse("rejected.json");

        var exception = Assert.Throws<BilibiliApiResponseException>(RequestSample);

        Assert.Equal("sample", exception.Operation);
        Assert.Contains("code=-101", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("error.html")]
    [InlineData("malformed.json")]
    public void NonJsonSamplesThrowTypedApiFailure(string sampleName)
    {
        ConfigureResponse(sampleName);

        Assert.Throws<BilibiliApiResponseException>(RequestSample);
    }

    public void Dispose()
    {
        BiliWebClient.ClearTestOverrides();
        GC.SuppressFinalize(this);
    }

    private static JObject RequestSample()
    {
        return BiliApiRequest.RequestJson<JObject>(
            "https://example.com/getLogin",
            referer: null,
            operationName: "sample",
            logTag: nameof(BiliApiContractSampleTests),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static void ConfigureResponse(string sampleName)
    {
        var body = File.ReadAllText(Path.Combine(SampleDirectory, sampleName));
        BiliWebClient.SendOverrideForTests = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        };
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
