namespace DownKyi.Core.Danmaku2Ass;

/// <summary>
/// 过滤器基类
/// </summary>
public abstract class Filter
{
    public abstract IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus);
}

/// <summary>
/// 顶部样式过滤器
/// </summary>
public class TopFilter : Filter
{
    public override IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus)
    {
        return danmakus.Where(danmaku => danmaku.Style != "top").ToList();
    }
}

/// <summary>
/// 底部样式过滤器
/// </summary>
public class BottomFilter : Filter
{
    public override IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus)
    {
        return danmakus.Where(danmaku => danmaku.Style != "bottom").ToList();
    }
}

/// <summary>
/// 滚动样式过滤器
/// </summary>
public class ScrollFilter : Filter
{
    public override IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus)
    {
        return danmakus.Where(danmaku => danmaku.Style != "scroll").ToList();
    }
}

/// <summary>
/// 自定义过滤器
/// </summary>
public class CustomFilter : Filter
{
    public override IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus)
    {
        // TODO
        throw new NotImplementedException("使用了过滤器的未实现的方法。");
    }
}
