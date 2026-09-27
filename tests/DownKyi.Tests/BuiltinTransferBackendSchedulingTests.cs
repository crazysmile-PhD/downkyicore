using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class BuiltinTransferBackendSchedulingTests
{
    [Theory]
    [InlineData(4, 8 * 1024 * 1024, 8)]
    [InlineData(4, 64 * 1024 * 1024, 16)]
    [InlineData(16, 8 * 1024 * 1024, 16)]
    [InlineData(16, 100 * 1024 * 1024, 64)]
    public void KnownSizeQueuesUsefulChunksWithoutIncreasingParallelConnections(
        int parallelCount,
        long expectedBytes,
        int expectedChunkCount)
    {
        var scheduling = BuiltinTransferBackend.CalculateChunkScheduling(
            parallelCount,
            expectedBytes);

        Assert.Equal(expectedChunkCount, scheduling.ChunkCount);
        Assert.Equal(0, scheduling.MinimumChunkSize);
    }

    [Fact]
    public void UnknownSizeLetsDownloaderBoundQueuedChunksAfterDiscovery()
    {
        var scheduling = BuiltinTransferBackend.CalculateChunkScheduling(
            parallelCount: 8,
            expectedBytes: 0);

        Assert.Equal(32, scheduling.ChunkCount);
        Assert.Equal(1024 * 1024, scheduling.MinimumChunkSize);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void InvalidInputsAreRejected(int parallelCount, long expectedBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuiltinTransferBackend.CalculateChunkScheduling(parallelCount, expectedBytes));
    }
}
