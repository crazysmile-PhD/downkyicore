using DownKyi.Core.BiliApi.Dynamic;

namespace DownKyi.Core.Tests;

public sealed class DynamicApiTests
{
    [Fact]
    public void ParseResponseReadsArchiveDrawAndPaginationFields()
    {
        const string json = """
            {
              "code": 0,
              "message": "0",
              "data": {
                "has_more": true,
                "offset": "next-offset",
                "items": [
                  {
                    "id_str": "1001",
                    "type": "DYNAMIC_TYPE_AV",
                    "visible": true,
                    "modules": {
                      "module_author": {
                        "mid": 42,
                        "name": "Uploader",
                        "face": "https://example.test/avatar.jpg",
                        "pub_action": "投稿了视频",
                        "pub_time": "1分钟前",
                        "pub_ts": 1700000000
                      },
                      "module_dynamic": {
                        "desc": { "text": "video text" },
                        "major": {
                          "type": "MAJOR_TYPE_ARCHIVE",
                          "archive": {
                            "bvid": "BV1test",
                            "cover": "https://example.test/cover.jpg",
                            "title": "Video title",
                            "desc": "Video description",
                            "duration_text": "03:21",
                            "jump_url": "//www.bilibili.com/video/BV1test"
                          }
                        }
                      },
                      "module_stat": {
                        "forward": { "count": 1 },
                        "comment": { "count": 2 },
                        "like": { "count": 3 }
                      }
                    }
                  },
                  {
                    "id_str": "1002",
                    "type": "DYNAMIC_TYPE_DRAW",
                    "modules": {
                      "module_dynamic": {
                        "major": {
                          "type": "MAJOR_TYPE_DRAW",
                          "draw": {
                            "items": [
                              { "src": "https://example.test/image.jpg", "width": 1920, "height": 1080 }
                            ]
                          }
                        }
                      }
                    }
                  }
                ]
              }
            }
            """;

        var data = DynamicFeedApi.ParseResponse(json);

        Assert.NotNull(data);
        Assert.True(data.HasMore);
        Assert.Equal("next-offset", data.Offset);
        Assert.Equal(2, data.Items.Count);
        Assert.Equal("BV1test", data.Items[0].Modules.Content.Major?.Archive?.Bvid);
        Assert.Equal(3, data.Items[0].Modules.Stats.Like.Count);
        Assert.Equal(
            "https://example.test/image.jpg",
            data.Items[1].Modules.Content.Major?.Draw?.Items[0].Source);
    }

    [Theory]
    [InlineData(-101)]
    [InlineData(-352)]
    public void ParseResponseRejectsApiFailures(int code)
    {
        var data = DynamicFeedApi.ParseResponse($$"""
            { "code": {{code}}, "message": "failed", "data": null }
            """);

        Assert.Null(data);
    }
}
