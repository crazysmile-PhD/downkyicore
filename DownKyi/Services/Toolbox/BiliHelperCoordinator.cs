using System.Threading;
using System.Threading.Tasks;
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
        if (string.IsNullOrEmpty(input) || !ParseEntrance.IsAvId(input))
        {
            return null;
        }

        var avid = ParseEntrance.GetAvId(input);
        return avid == -1 ? null : BvId.Av2Bv(avid);
    }

    public string? ConvertBvidToAvid(string? input)
    {
        if (string.IsNullOrEmpty(input) || !ParseEntrance.IsBvId(input))
        {
            return null;
        }

        return $"av{BvId.Bv2Av(input)}";
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
