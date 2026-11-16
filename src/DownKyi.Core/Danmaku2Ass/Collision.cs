namespace DownKyi.Core.Danmaku2Ass;

/// <summary>
/// 碰撞处理
/// </summary>
public class Collision
{
    private readonly int lineCount;
    private readonly List<int> leaves;

    public Collision(int lineCount)
    {
        this.lineCount = lineCount;
        leaves = Leaves();
    }

    private List<int> Leaves()
    {
        var ret = new List<int>(lineCount);
        for (var i = 0; i < lineCount; i++) ret.Add(0);
        return ret;
    }

    /// <summary>
    /// 碰撞检测
    /// 返回行号和时间偏移
    /// </summary>
    /// <param name="display"></param>
    /// <returns></returns>
    public Tuple<int, float> Detect(Display display)
    {
        ArgumentNullException.ThrowIfNull(display);

        var overlaps = new List<float>(leaves.Count);
        for (var i = 0; i < leaves.Count; i++)
        {
            var overlap = leaves[i] - display.Danmaku.Start;
            // 某一行有足够空间，直接返回行号和 0 偏移
            if (overlap <= 0)
            {
                return Tuple.Create(i, 0f);
            }

            overlaps.Add(overlap);
        }

        // 所有行都会碰撞时，选择时间重叠最少的一行
        var leastOverlap = overlaps.Min();
        var lineIndex = overlaps.IndexOf(leastOverlap);
        return Tuple.Create(lineIndex, leastOverlap);
    }

    public void Update(float leave, int lineIndex, float offset)
    {
        var occupiedUntil = DanmakuAssFormatting.IntCeiling(leave + offset);
        leaves[lineIndex] = Math.Max(leaves[lineIndex], occupiedUntil);
    }
}
