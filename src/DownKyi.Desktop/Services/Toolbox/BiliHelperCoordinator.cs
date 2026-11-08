using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Media;
using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Services.Toolbox;

internal interface IBiliHelperCoordinator
{
    string? ConvertAvidToBvid(string? input);

    string? ConvertBvidToAvid(string? input);

    Task<string?> FindDanmakuSenderAsync(string? userId, CancellationToken cancellationToken);
}

internal sealed class BiliHelperCoordinator : IBiliHelperCoordinator
{
    public string? ConvertAvidToBvid(string? input)
    {
        if (BilibiliInputParser.Parse(input) is not BilibiliAvidInput avid)
        {
            return null;
        }

        return BvId.Av2Bv(avid.Avid);
    }

    public string? ConvertBvidToAvid(string? input)
    {
        if (BilibiliInputParser.Parse(input) is not BilibiliBvidInput bvid)
        {
            return null;
        }

        return $"av{BvId.Bv2Av(bvid.Bvid)}";
    }

    public Task<string?> FindDanmakuSenderAsync(string? userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.Run(
            () => (string?)DanmakuSender.FindDanmakuSender(userId, cancellationToken),
            cancellationToken);
    }
}
