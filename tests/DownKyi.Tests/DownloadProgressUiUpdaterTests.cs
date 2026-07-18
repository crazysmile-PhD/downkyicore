using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadProgressUiUpdaterTests
{
    [Fact]
    public void ProgressEventsAreBoundedAndCompletionIsAlwaysPublished()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var updater = new DownloadProgressUiUpdater(clock, TimeSpan.FromMilliseconds(100));
        var item = new DownloadingItem { Downloading = new Downloading() };
        var notifications = 0;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DownloadingItem.Progress)
                or nameof(DownloadingItem.DownloadingFileSize)
                or nameof(DownloadingItem.SpeedDisplay))
            {
                notifications++;
            }
        };

        var published = 0;
        for (var millisecond = 0; millisecond < 1000; millisecond++)
        {
            if (updater.TryUpdate(item, millisecond / 10d, millisecond, 1000, millisecond))
            {
                published++;
            }

            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.True(updater.TryUpdate(item, 100, 1000, 1000, 5000));
        Assert.Equal(11, published + 1);
        Assert.Equal(33, notifications);
        Assert.Equal(100, item.Progress);
        Assert.Equal(5000, item.Downloading.MaxSpeed);
    }

    [Fact]
    public void SuppressedUiSamplesStillTrackMaximumSpeed()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var updater = new DownloadProgressUiUpdater(clock, TimeSpan.FromSeconds(1));
        var item = new DownloadingItem { Downloading = new Downloading() };

        Assert.True(updater.TryUpdate(item, 1, 1, 100, 100));
        Assert.False(updater.TryUpdate(item, 2, 2, 100, 10_000));

        Assert.Equal(10_000, item.Downloading.MaxSpeed);
        Assert.Equal(1, item.Progress);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
