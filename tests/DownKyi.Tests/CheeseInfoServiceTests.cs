using DownKyi.Application.Media;
using DownKyi.Services;

namespace DownKyi.Tests;

public sealed class CheeseInfoServiceTests
{
    [Fact]
    public async Task VideoPagesUseEpisodeDurationFromDetailMetadata()
    {
        const string response =
            """
            {
              "code": 0,
              "message": "success",
              "data": {
                "episodes": [
                  { "aid": 1, "cid": 2, "id": 3, "title": "Lesson", "duration": 2185 },
                  { "aid": 1, "cid": 4, "id": 5, "title": "Unknown", "duration": 0 }
                ]
              }
            }
            """;
        using var settings = new TestSettingsStore();
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(response)
        };
        var service = await CheeseInfoService.CreateAsync(
            new BilibiliCheeseSeasonInput(13244),
            settings.Store,
            client,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            service.GetVideoPages(TestContext.Current.CancellationToken),
            page => Assert.Equal("36m25s", page.Duration),
            page => Assert.Equal("N/A", page.Duration));
    }
}
