using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Bilibili;
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
    private readonly IBilibiliApiClient _client;

    public VideoTagProvider(IBilibiliApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<IReadOnlyList<string>> GetTagsAsync(
        string bvid,
        long cid,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bvid);
        cancellationToken.ThrowIfCancellationRequested();
        var tags = await _client.GetBiliTagInfoAsync(bvid, cid, cancellationToken)
            .ConfigureAwait(false);
        return tags?
            .Where(static tag => tag != null && !string.IsNullOrWhiteSpace(tag.TagName))
            .Select(static tag => tag.TagName)
            .ToArray()
            ?? Array.Empty<string>();
    }
}
