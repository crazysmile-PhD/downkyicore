using DownKyi.Application.Downloads;

namespace DownKyi.Application.Tests;

public sealed class DownloadAddCoordinatorTests
{
    [Fact]
    public async Task CancelingDirectorySelectionDoesNotAddADownload()
    {
        var addWasCalled = false;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
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
}
