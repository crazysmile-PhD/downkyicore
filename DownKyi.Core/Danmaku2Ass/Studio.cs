namespace DownKyi.Core.Danmaku2Ass;

/// <summary>
/// 字幕工程类
/// </summary>
public class Studio
{
    private readonly System.Text.Encoding _outputEncoding;
    public Config Config { get; }
    public IReadOnlyList<Danmaku> Danmakus { get; }

    public Creater Creater { get; private set; } = null!;
    public int KeepedCount { get; private set; }
    public int DropedCount { get; private set; }

    public Studio(Config config, IReadOnlyList<Danmaku> danmakus)
        : this(config, danmakus, new System.Text.UTF8Encoding(false))
    {
    }

    internal Studio(Config config, IReadOnlyList<Danmaku> danmakus, System.Text.Encoding outputEncoding)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(danmakus);
        ArgumentNullException.ThrowIfNull(outputEncoding);
        Config = config;
        Danmakus = danmakus;
        _outputEncoding = outputEncoding;
    }

    public void StartHandle()
    {
        Creater = SetCreater();
        KeepedCount = SetKeepedCount();
        DropedCount = SetDropedCount();
    }

    /// <summary>
    /// ass 创建器
    /// </summary>
    /// <returns></returns>
    protected Creater SetCreater()
    {
        return new Creater(Config, Danmakus);
    }

    /// <summary>
    /// 保留条数
    /// </summary>
    /// <returns></returns>
    protected int SetKeepedCount()
    {
        return Creater.Subtitles.Count;
    }

    /// <summary>
    /// 丢弃条数
    /// </summary>
    /// <returns></returns>
    protected int SetDropedCount()
    {
        return Danmakus.Count - KeepedCount;
    }

    /// <summary>
    /// 创建 ass 字幕
    /// </summary>
    /// <param name="fileName"></param>
    public void CreateAssFile(string fileName)
    {
        CreateFile(fileName, Creater.Text);
    }

    public void CreateFile(string fileName, string text)
    {
        File.WriteAllText(fileName, text, _outputEncoding);
    }

    public Dictionary<string, int> Report()
    {
        return new Dictionary<string, int>
        {
            { "total", Danmakus.Count },
            { "droped", DropedCount },
            { "keeped", KeepedCount },
        };
    }
}
