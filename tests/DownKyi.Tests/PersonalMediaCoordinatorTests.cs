using DownKyi.Core.BiliApi.History.Models;
using DownKyi.Services.Media;
using Prism.Events;

namespace DownKyi.Tests;

public sealed class PersonalMediaCoordinatorTests
{
    [Fact]
    public async Task PreCanceledToViewLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = new PersonalMediaCoordinator(settings.Store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadToViewAsync(new EventAggregator(), cancellation.Token));
    }

    [Fact]
    public async Task PreCanceledHistoryLoadDoesNotStartApiWork()
    {
        using var settings = new TestSettingsStore();
        var coordinator = new PersonalMediaCoordinator(settings.Store);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadHistoryPageAsync(0, 0, 30, new EventAggregator(), cancellation.Token));
    }

    [Fact]
    public void HistoryMappingPreservesSupportedAddressAndNormalizesCover()
    {
        using var settings = new TestSettingsStore();
        var source = new HistoryList
        {
            Title = "video",
            Cover = "//example.invalid/cover.jpg",
            History = new HistoryListHistory
            {
                Business = "archive",
                Bvid = "BV1test"
            }
        };

        var media = PersonalMediaCoordinator.ConvertHistory(source, new EventAggregator(), settings.Store);

        Assert.NotNull(media);
        Assert.Equal("https://www.bilibili.com/video/BV1test", media.Url);
        Assert.Equal("https://example.invalid/cover.jpg", media.Cover);
    }

    [Fact]
    public void HistoryMappingRejectsUnsupportedBusiness()
    {
        using var settings = new TestSettingsStore();
        var source = new HistoryList
        {
            History = new HistoryListHistory { Business = "article" }
        };

        Assert.Null(PersonalMediaCoordinator.ConvertHistory(source, new EventAggregator(), settings.Store));
    }
}
