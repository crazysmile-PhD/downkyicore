using Bilibili.Community.Service.Dm.V1;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.DanmakuApi;
using Google.Protobuf;

namespace DownKyi.Core.Tests;

public sealed class DanmakuSegmentContractTests
{
    [Fact]
    public async Task AdvertisedSegmentCountIncludesEmptyInteriorSegments()
    {
        var payloads = new Queue<byte[]>(
        [
            new DmWebViewReply
            {
                DmSge = new DmSegConfig { PageSize = 360_000, Total = 3 }
            }.ToByteArray(),
            new DmSegMobileReply
            {
                Elems =
                {
                    new DanmakuElem
                    {
                        Id = 1,
                        Progress = 1_000,
                        Mode = 1,
                        Fontsize = 25,
                        Color = 0xFFFFFF,
                        MidHash = "anonymous",
                        Content = "hello"
                    }
                }
            }.ToByteArray(),
            new DmSegMobileReply().ToByteArray(),
            new DmSegMobileReply
            {
                Elems =
                {
                    new DanmakuElem
                    {
                        Id = 2,
                        Progress = 800_000,
                        Mode = 1,
                        Fontsize = 25,
                        Color = 0xFFFFFF,
                        MidHash = "anonymous",
                        Content = "after quiet bucket"
                    }
                }
            }.ToByteArray()
        ]);
        var requests = new List<string>();
        var client = new StubBilibiliApiClient(
            static (_, _) => Task.FromException<string>(new NotSupportedException()),
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                requests.Add(request.RequestAddress);
                if (payloads.Count == 0)
                {
                    return Task.FromException<Stream>(
                        new InvalidOperationException("Danmaku enumeration did not terminate."));
                }

                return Task.FromResult<Stream>(new MemoryStream(payloads.Dequeue()));
            });

        var result = await client.GetAllDanmakuProtoAsync(
            avid: 11,
            cid: 22,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(["hello", "after quiet bucket"], result.Select(item => item.Content));
        Assert.Equal(4, requests.Count);
        Assert.Contains("/x/v2/dm/web/view", requests[0], StringComparison.Ordinal);
        Assert.Contains("segment_index=1", requests[1], StringComparison.Ordinal);
        Assert.Contains("segment_index=2", requests[2], StringComparison.Ordinal);
        Assert.Contains("segment_index=3", requests[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZeroAdvertisedSegmentsDoesNotGuessAnExtraRequest()
    {
        var requests = new List<string>();
        var client = CreateClient(
            new DmWebViewReply
            {
                DmSge = new DmSegConfig { PageSize = 360_000, Total = 0 }
            }.ToByteArray(),
            requests);

        var result = await client.GetAllDanmakuProtoAsync(
            avid: 11,
            cid: 22,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Empty(result);
        Assert.Single(requests);
        Assert.Contains("/x/v2/dm/web/view", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSegmentMetadataFailsInsteadOfGuessingTermination()
    {
        var client = CreateClient(new DmWebViewReply().ToByteArray(), []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetAllDanmakuProtoAsync(
                avid: 11,
                cid: 22,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData((long)int.MaxValue)]
    [InlineData(long.MaxValue)]
    public async Task UnsafeSegmentBoundsFailBeforeRequestingAnySegment(long total)
    {
        var requests = new List<string>();
        var client = CreateClient(
            new DmWebViewReply
            {
                DmSge = new DmSegConfig { PageSize = 360_000, Total = total }
            }.ToByteArray(),
            requests);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetAllDanmakuProtoAsync(
                avid: 11,
                cid: 22,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Single(requests);
        Assert.Contains("/x/v2/dm/web/view", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSegmentMetadataFailsInsteadOfGuessingTermination()
    {
        var client = CreateClient([0x0A, 0x05, 0x01], []);

        await Assert.ThrowsAsync<InvalidProtocolBufferException>(() =>
            client.GetAllDanmakuProtoAsync(
                avid: 11,
                cid: 22,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private static StubBilibiliApiClient CreateClient(
        byte[] payload,
        List<string> requests)
    {
        return new StubBilibiliApiClient(
            static (_, _) => Task.FromException<string>(new NotSupportedException()),
            (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                requests.Add(request.RequestAddress);
                return Task.FromResult<Stream>(new MemoryStream(payload));
            });
    }
}
