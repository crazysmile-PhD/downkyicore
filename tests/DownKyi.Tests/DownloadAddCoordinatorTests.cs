using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadAddCoordinatorTests
{
    [Fact]
    public async Task AddToDownloadIfDirectorySelectedAsync_DoesNotAdd_WhenDirectorySelectionIsCanceled()
    {
        var addWasCalled = false;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => Task.FromResult<string?>(null),
            _ =>
            {
                addWasCalled = true;
                return Task.FromResult(1);
            });

        Assert.Null(result);
        Assert.False(addWasCalled);
    }

    [Fact]
    public async Task AddToDownloadIfDirectorySelectedAsync_Adds_WhenDirectoryIsSelected()
    {
        string? receivedDirectory = null;

        var result = await DownloadAddCoordinator.AddToDownloadIfDirectorySelectedAsync(
            () => Task.FromResult<string?>("D:\\Downloads"),
            directory =>
            {
                receivedDirectory = directory;
                return Task.FromResult(2);
            });

        Assert.Equal(2, result);
        Assert.Equal("D:\\Downloads", receivedDirectory);
    }
}
