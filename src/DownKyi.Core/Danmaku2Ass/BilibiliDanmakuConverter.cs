using System.Collections.Frozen;
using DownKyi.Application.Bilibili;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.DanmakuApi;
using DownKyi.Core.BiliApi.DanmakuApi.Models;

namespace DownKyi.Core.Danmaku2Ass;

public sealed class BilibiliDanmakuConverter
{
    private const int NormalFontSize = 25;
    private static readonly FrozenDictionary<int, string> StyleByMode =
        new Dictionary<int, string>
        {
            [0] = "none",
            [1] = "scroll",
            [2] = "scroll",
            [3] = "scroll",
            [4] = "bottom",
            [5] = "top",
            [6] = "scroll",
            [7] = "none",
            [8] = "none",
            [9] = "none",
            [10] = "none",
            [11] = "none",
            [12] = "none",
            [13] = "none",
            [14] = "none",
            [15] = "none"
        }.ToFrozenDictionary();
    private readonly Dictionary<string, bool> _config = new(StringComparer.Ordinal)
    {
        { "top_filter", false },
        { "bottom_filter", false },
        { "scroll_filter", false }
    };
    private CustomDanmakuFilter? _customFilter;

    /// <summary>
    /// 是否屏蔽顶部弹幕
    /// </summary>
    /// <param name="isFilter"></param>
    /// <returns></returns>
    public BilibiliDanmakuConverter SetTopFilter(bool isFilter)
    {
        _config["top_filter"] = isFilter;
        return this;
    }

    /// <summary>
    /// 是否屏蔽底部弹幕
    /// </summary>
    /// <param name="isFilter"></param>
    /// <returns></returns>
    public BilibiliDanmakuConverter SetBottomFilter(bool isFilter)
    {
        _config["bottom_filter"] = isFilter;
        return this;
    }

    /// <summary>
    /// 是否屏蔽滚动弹幕
    /// </summary>
    /// <param name="isFilter"></param>
    /// <returns></returns>
    public BilibiliDanmakuConverter SetScrollFilter(bool isFilter)
    {
        _config["scroll_filter"] = isFilter;
        return this;
    }

    public BilibiliDanmakuConverter SetCustomFilter(
        bool removeEmojiAndSpecialCharacters,
        IEnumerable<string> blockedKeywords,
        IEnumerable<long> blockedSenderUids)
    {
        ArgumentNullException.ThrowIfNull(blockedKeywords);
        ArgumentNullException.ThrowIfNull(blockedSenderUids);

        var blockedCommenters = blockedSenderUids
            .Where(userId => userId > 0)
            .Distinct()
            .Select(DanmakuSender.GetMidHash);
        var customFilter = new CustomDanmakuFilter(
            removeEmojiAndSpecialCharacters,
            blockedKeywords,
            blockedCommenters);
        _customFilter = customFilter.IsEnabled ? customFilter : null;
        return this;
    }

    public async Task<IReadOnlyList<BiliDanmaku>> GetDanmakusAsync(
        IBilibiliApiClient client,
        long avid,
        long cid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var biliDanmakus = (await client.GetAllDanmakuProtoAsync(
                avid,
                cid,
                cancellationToken).ConfigureAwait(false))
            .OrderBy(danmaku => danmaku.Progress)
            .ToArray();
        return CreateSurvivingSnapshot(biliDanmakus, cancellationToken);
    }

    private List<BiliDanmaku> CreateSurvivingSnapshot(
        BiliDanmaku[] biliDanmakus,
        CancellationToken cancellationToken)
    {
        var survivingDanmakus = new List<BiliDanmaku>(biliDanmakus.Length);
        foreach (var biliDanmaku in biliDanmakus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExplicitlyFiltered(biliDanmaku.Mode)
                || _customFilter?.IsExplicitlyExcluded(
                    biliDanmaku.Content,
                    biliDanmaku.MidHash,
                    cancellationToken) == true)
            {
                continue;
            }

            survivingDanmakus.Add(biliDanmaku);
        }

        return survivingDanmakus;
    }

    public string CreateAss(
        Config subtitleConfig,
        IReadOnlyList<BiliDanmaku> biliDanmakus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subtitleConfig);
        ArgumentNullException.ThrowIfNull(biliDanmakus);
        cancellationToken.ThrowIfCancellationRequested();
        // 弹幕转换

        var danmakus = new List<Danmaku>();
        foreach (var biliDanmaku in biliDanmakus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var danmaku = new Danmaku
            {
                // biliDanmaku.Progress单位是毫秒，所以除以1000，单位变为秒
                Start = biliDanmaku.Progress / 1000.0f,
                Style = StyleByMode[biliDanmaku.Mode],
                Color = (int)biliDanmaku.Color,
                Commenter = biliDanmaku.MidHash,
                Content = biliDanmaku.Content,
                SizeRatio = 1.0f * biliDanmaku.Fontsize / NormalFontSize
            };

            danmakus.Add(danmaku);
        }

        // 弹幕预处理
        var producer = new Producer(_config, danmakus, _customFilter, cancellationToken);
        producer.StartHandle();

        // 字幕生成
        return new Creater(subtitleConfig, producer.KeepedDanmakus).Text;
    }

    private bool IsExplicitlyFiltered(int mode)
    {
        return (mode == 5 && _config["top_filter"])
               || (mode == 4 && _config["bottom_filter"])
               || (mode is 1 or 2 or 3 or 6 && _config["scroll_filter"]);
    }

}
