using DownKyi.Application.Downloads;

namespace DownKyi.Application.Tests;

public sealed class DownloadAddCoordinatorTests
{
    [Fact]
    public async Task CancelingDirectorySelectionDoesNotAddADownload()
    {
        var addWasCalled = false;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => Task.FromResult(true),
            () => Task.FromResult<string?>(null),
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
    public async Task SelectedDirectoryReachesTheDownloadOperation()
    {
        string? receivedDirectory = null;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => Task.FromResult(true),
            () => Task.FromResult<string?>("D:\\Downloads"),
            directory =>
            {
                receivedDirectory = directory;
                return Task.FromResult(2);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result);
        Assert.Equal("D:\\Downloads", receivedDirectory);
    }

    [Fact]
    public async Task BlockedAdmissionStopsBeforeDirectorySelectionAndBatchAdd()
    {
        var directorySelectionCount = 0;
        var addCount = 0;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => Task.FromResult(false),
            () =>
            {
                directorySelectionCount++;
                return Task.FromResult<string?>("D:\\Downloads");
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
