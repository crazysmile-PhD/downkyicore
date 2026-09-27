using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Tests;

public sealed class DownloadAddCoordinatorTests
{
    [Fact]
    public async Task CancelingDirectorySelectionDoesNotAddADownload()
    {
        var addWasCalled = false;

        var result = await DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => Task.FromResult(true),
            () => Task.FromResult<DownloadAddSelection?>(null),
            _ =>
            {
                addWasCalled = true;
                return Task.FromResult(1);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(addWasCalled);
    }

    [Fact]
    public async Task AcceptedSelectionReachesTheDownloadOperation()
    {
        DownloadAddSelection? receivedSelection = null;
        var requestedContent = DownloadContentSelection.None with { Video = true };

        var result = await DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => Task.FromResult(true),
            () => Task.FromResult<DownloadAddSelection?>(
                new DownloadAddSelection("D:\\Downloads", requestedContent)),
            selection =>
            {
                receivedSelection = selection;
                return Task.FromResult(2);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Equal("D:\\Downloads", receivedSelection!.Directory);
        Assert.Same(requestedContent, receivedSelection.RequestedContent);
    }

    [Fact]
    public async Task BlockedAdmissionStopsBeforeDirectorySelectionAndBatchAdd()
    {
        var directorySelectionCount = 0;
        var addCount = 0;

        var result = await DownloadAddCoordinator.AddToDownloadIfSelectionAcceptedAsync(
            () => Task.FromResult(false),
            () =>
            {
                directorySelectionCount++;
                return Task.FromResult<DownloadAddSelection?>(new DownloadAddSelection(
                    "D:\\Downloads",
                    DownloadContentSelection.All));
            },
            _ =>
            {
                addCount++;
                return Task.FromResult(2);
            },
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, directorySelectionCount);
        Assert.Equal(0, addCount);
    }
}
