using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Media;
using DownKyi.Services.UserSpace;

namespace DownKyi.Tests;

public sealed class SeasonsSeriesCoordinatorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreCanceledPageRequestDoesNotStartUserSpaceApiWork(int kindValue)
    {
        var coordinator = new SeasonsSeriesCoordinator(
            new ContentDownloadCoordinator(new ThrowingFactory(), new ThrowingInfoServiceFactory()));
        var kind = (SeasonsSeriesKind)kindValue;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => coordinator.LoadPageAsync(42, 24, kind, 1, 30, cancellation.Token));
    }

    private sealed class ThrowingFactory : IAddToDownloadServiceFactory
    {
        public IAddToDownloadSession Create(PlayStreamType streamType)
        {
            throw new InvalidOperationException("Page loading must not create a download session.");
        }

        public IAddToDownloadSession Create(string id, PlayStreamType streamType)
        {
            throw new InvalidOperationException("Page loading must not create a download session.");
        }
    }

    private sealed class ThrowingInfoServiceFactory : IContentInfoServiceFactory
    {
        public IInfoService Create(ContentDownloadItem item, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Page loading must not create an info service.");
        }
    }
}
