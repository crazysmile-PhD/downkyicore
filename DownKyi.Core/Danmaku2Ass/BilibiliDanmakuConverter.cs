using System.Collections.Frozen;
using DownKyi.Core.BiliApi.DanmakuApi;

namespace DownKyi.Core.Danmaku2Ass;

public sealed class BilibiliDanmakuConverter
{
    private const int NormalFontSize = 25;
    private static readonly FrozenDictionary<int, VideoResolution> Resolutions =
        new Dictionary<int, VideoResolution>
        {
            [6] = new(426, 240),
            [16] = new(640, 360),
            [32] = new(854, 480),
            [64] = new(1280, 720),
            [74] = new(1280, 720),
            [80] = new(1920, 1080),
            [112] = new(1920, 1080),
            [116] = new(1920, 1080),
            [120] = new(3840, 2160)
        }.ToFrozenDictionary();
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

    public void Create(long avid, long cid, Config subtitleConfig, string assFile, CancellationToken cancellationToken = default)
    {
        // 弹幕转换
        var biliDanmakus = DanmakuProtobuf.GetAllDanmakuProto(avid, cid, cancellationToken)
            .OrderBy(danmaku => danmaku.Progress);

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
        var producer = new Producer(_config, danmakus);
        producer.StartHandle();

        // 字幕生成
        var keepedDanmakus = producer.KeepedDanmakus;
        var studio = new Studio(subtitleConfig, keepedDanmakus);
        studio.StartHandle();
        studio.CreateAssFile(assFile);
    }

    public static Dictionary<string, int> GetResolution(int quality)
    {
        var resolution = Resolutions.GetValueOrDefault(quality);
        return new Dictionary<string, int>
        {
            ["width"] = resolution.Width,
            ["height"] = resolution.Height
        };
    }

    private readonly record struct VideoResolution(int Width, int Height);
}
