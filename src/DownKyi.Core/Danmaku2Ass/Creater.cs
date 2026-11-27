using System.Globalization;
using System.Text;

namespace DownKyi.Core.Danmaku2Ass;

/// <summary>
/// 创建器
/// </summary>
public class Creater
{
    private readonly CancellationToken _cancellationToken;
    public Config Config { get; }
    public IReadOnlyList<Danmaku> Danmakus { get; }
    public IReadOnlyList<Subtitle> Subtitles { get; }
    public string Text { get; }

    public Creater(Config config, IReadOnlyList<Danmaku> danmakus)
        : this(config, danmakus, CancellationToken.None)
    {
    }

    public Creater(Config config, IReadOnlyList<Danmaku> danmakus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(danmakus);
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken = cancellationToken;
        Config = config;
        Danmakus = danmakus;
        Subtitles = SetSubtitles();
        Text = SetText();
    }

    protected IReadOnlyList<Subtitle> SetSubtitles()
    {
        var scroll = new Collision(Config.LineCount);
        var stayed = new Collision(Config.LineCount);
        var collisions = new Dictionary<string, Collision>
        {
            { "scroll", scroll },
            { "top", stayed },
            { "bottom", stayed }
        };

        var subtitles = new List<Subtitle>(Danmakus.Count);
        foreach (var danmaku in Danmakus)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            // 丢弃不支持的
            if (danmaku.Style == "none")
            {
                continue;
            }

            // 创建显示方式对象
            var display = Display.Factory(Config, danmaku);
            var collision = collisions[danmaku.Style];
            var (lineIndex, overlap) = collision.Detect(display);

            // 保留容忍范围内的等待行为；超出时改为在原时间重叠最少的轨道显示。
            var waitingOffset = overlap <= Config.DropOffset ? overlap : 0f;

            // 接受偏移，更新碰撞信息
            display.Relayout(lineIndex);
            collision.Update(display.Leave, lineIndex, waitingOffset);

            // 再加上自定义偏移
            var offset = waitingOffset + Config.CustomOffset;
            var subtitle = new Subtitle(danmaku, display, offset);

            subtitles.Add(subtitle);
        }

        return subtitles;
    }

    protected string SetText()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var header = Config.HeaderTemplate
            .Replace("{title}", Config.Title, StringComparison.Ordinal)
            .Replace("{width}", Config.ScreenWidth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{height}", Config.ScreenHeight.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fontname}", Config.FontName, StringComparison.Ordinal)
            .Replace("{fontsize}", Config.BaseFontSize.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var text = new StringBuilder(header);
        foreach (var subtitle in Subtitles)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            text.Append('\n').Append(subtitle.Text);
        }

        _cancellationToken.ThrowIfCancellationRequested();
        var result = text.ToString();
        _cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
