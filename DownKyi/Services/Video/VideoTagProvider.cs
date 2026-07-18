using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.BiliApi.Video;

namespace DownKyi.Services.Video;

internal interface IVideoTagProvider
{
    Task<IReadOnlyList<string>> GetTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken);
}

internal sealed class VideoTagProvider : IVideoTagProvider
{
    public Task<IReadOnlyList<string>> GetTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvid);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<string>>(
            () => VideoInfo.GetBiliTagInfo(bvid, cid, cancellationToken)
                ?.Select(tag => tag.TagName)
                .ToArray() ?? Array.Empty<string>(),
            cancellationToken);
    }
}
