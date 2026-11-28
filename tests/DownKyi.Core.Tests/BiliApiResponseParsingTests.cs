using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.Video.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DownKyi.Core.Tests;

public sealed class BiliApiResponseParsingTests
{
    [Theory]
    [InlineData("data")]
    [InlineData("result")]
    public void BusinessPayloadSurvivesMetadataValidation(string field)
    {
        var nested = new string('[', 50) + "\"字幕🎵\"" + new string(']', 50);
        var response = $$"""{"{{field}}":{"nested":{{nested}}},"code":0,"message":"成功"}""";

        var result = BiliApiRequest.ParseJson<JObject>(response, "payload");

        Assert.Equal(JToken.Parse(nested), result[field]?["nested"], JToken.EqualityComparer);
    }

    [Fact]
    public void RejectedCodeTakesPriorityOverBusinessSchemaFailure()
    {
        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BiliApiRequest.ParseJson<VideoViewOrigin>(
                """{"data":"invalid video","code":-101,"message":"未登录"}""", "video"));

        Assert.Equal("video", exception.Operation);
        Assert.Equal(-101, exception.Code);
        Assert.Contains("message=未登录", exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ExplicitlyAllowedCodeStillParsesBusinessPayload()
    {
        var result = BiliApiRequest.ParseJson<VideoViewOrigin>(
            """{"code":-101,"data":{"title":"public payload"}}""", "video", allowedNonSuccessCode: -101);

        Assert.Equal("public payload", result.Data?.Title);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-101, -101)]
    public void AcceptedCodeRetainsBusinessSchemaFailure(int code, int? allowedCode)
    {
        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BiliApiRequest.ParseJson<VideoViewOrigin>(
                $$"""{"code":{{code}},"data":"invalid video"}""", "video", allowedNonSuccessCode: allowedCode));

        Assert.Equal("video", exception.Operation);
        Assert.Null(exception.Code);
        Assert.Contains("invalid JSON schema", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("""{"code":-101,"data":[1,]}""")]
    [InlineData("""{"code":0,"result":{"nested":}}""")]
    [InlineData("""{"code":0,"data":{}} trailing""")]
    public void MalformedPayloadRemainsMalformedEvenWhenMetadataIsReadable(string response)
    {
        AssertMalformed(response);
    }

    [Fact]
    public void ExcessivePayloadDepthStillFailsMetadataValidation()
    {
        AssertMalformed("{\"code\":0,\"data\":" + new string('[', 80) + "0" + new string(']', 80) + "}");
    }

    [Fact]
    public void BusinessParserStillUsesCallerSerializerSettings()
    {
        var settings = new JsonSerializerSettings { Converters = [new VideoTitleConverter()] };

        var result = BiliApiRequest.ParseJson<VideoViewOrigin>(
            """{"code":0,"data":{"title":"字幕🎵"}}""", "video", settings);

        Assert.Equal("converted:字幕🎵", result.Data?.Title);
    }

    private static void AssertMalformed(string response)
    {
        var exception = Assert.Throws<BilibiliApiResponseException>(() =>
            BiliApiRequest.ParseJson<JObject>(response, "payload"));

        Assert.Equal("payload", exception.Operation);
        Assert.Null(exception.Code);
        Assert.Contains("malformed JSON", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    private sealed class VideoTitleConverter : JsonConverter<VideoView>
    {
        public override VideoView ReadJson(JsonReader reader, Type objectType, VideoView? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            return new VideoView { Title = "converted:" + JObject.Load(reader).Value<string>("title") };
        }

        public override void WriteJson(JsonWriter writer, VideoView? value, JsonSerializer serializer)
        {
            throw new NotSupportedException();
        }
    }
}
