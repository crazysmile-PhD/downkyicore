using Bilibili.Community.Service.Dm.V1;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.DanmakuApi;
using Google.Protobuf;

namespace DownKyi.Core.Tests;

public sealed class DanmakuSegmentContractTests
{
    [Fact]
    public async Task EmptySuccessfulSegmentTerminatesEnumerationWithoutRequestingAnotherPage()
    {
        var payloads = new Queue<byte[]>(
        [
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
            new DmSegMobileReply().ToByteArray()
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

        Assert.Equal("hello", Assert.Single(result).Content);
        Assert.Equal(2, requests.Count);
        Assert.Contains("segment_index=1", requests[0], StringComparison.Ordinal);
        Assert.Contains("segment_index=2", requests[1], StringComparison.Ordinal);
    }
}
