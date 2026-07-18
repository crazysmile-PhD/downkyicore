using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Video.Models;
using Newtonsoft.Json;

namespace DownKyi.Core.Tests;

public sealed class JsonEnvelopePresenceTests
{
    [Fact]
    public void AriaResultOnlyResponseKeepsErrorMissing()
    {
        const string json = """
            {"id":"fixture","jsonrpc":"2.0","result":"gid-1"}
            """;

        var response = AriaClient.DeserializeRpcResponse<AriaAddUri>(json);

        Assert.Equal("gid-1", response.Result);
        Assert.Null(response.Error);
    }

    [Fact]
    public void AriaErrorOnlyResponseKeepsResultMissing()
    {
        const string json = """
            {"id":"fixture","jsonrpc":"2.0","error":{"code":1,"message":"Unauthorized"}}
            """;

        var response = AriaClient.DeserializeRpcResponse<AriaAddUri>(json);

        Assert.Null(response.Result);
        Assert.Equal(1, response.Error?.Code);
        Assert.Equal("Unauthorized", response.Error?.Message);
    }

    [Fact]
    public void AriaResponseMissingResultAndErrorDoesNotInventEitherField()
    {
        const string json = """
            {"id":"fixture","jsonrpc":"2.0"}
            """;

        var response = AriaClient.DeserializeRpcResponse<AriaAddUri>(json);

        Assert.Null(response.Result);
        Assert.Null(response.Error);
    }

    [Fact]
    public void AriaNullJsonCannotBecomeDefaultSuccessResponse()
    {
        Assert.Throws<JsonSerializationException>(() =>
            AriaClient.DeserializeRpcResponse<AriaAddUri>("null"));
    }

    [Fact]
    public void MissingBilibiliDataRemainsNull()
    {
        var response = JsonConvert.DeserializeObject<VideoViewOrigin>("{\"code\":0}");

        Assert.NotNull(response);
        Assert.Null(response.Data);
    }

    [Fact]
    public void RequiredBilibiliPayloadRejectsMissingFieldWithOperationContext()
    {
        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BiliApiRequest.RequirePayload<VideoView>(
                null,
                fieldName: "data",
                operationName: "fixture-operation"));

        Assert.Equal("fixture-operation", exception.Operation);
        Assert.Contains("required 'data'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredBilibiliPayloadReturnsExistingObject()
    {
        var payload = new VideoView();

        Assert.Same(
            payload,
            BiliApiRequest.RequirePayload(payload, operationName: "fixture-operation"));
    }
}
