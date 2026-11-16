using System.Globalization;
using System.Text;

namespace DownKyi.Core.Danmaku2Ass;

/// <summary>
/// 过滤器基类
/// </summary>
public abstract class Filter
{
    public abstract IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus);

    public virtual IReadOnlyList<Danmaku> DoFilter(
        IReadOnlyList<Danmaku> danmakus,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DoFilter(danmakus);
    }
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
/// 自定义弹幕过滤器
/// </summary>
public sealed class CustomDanmakuFilter : Filter
{
    private readonly bool _removeEmojiAndSpecialCharacters;
    private readonly string[] _blockedKeywords;
    private readonly HashSet<string> _blockedCommenters;

    public CustomDanmakuFilter(
        bool removeEmojiAndSpecialCharacters,
        IEnumerable<string> blockedKeywords,
        IEnumerable<string> blockedCommenters)
    {
        ArgumentNullException.ThrowIfNull(blockedKeywords);
        ArgumentNullException.ThrowIfNull(blockedCommenters);

        _removeEmojiAndSpecialCharacters = removeEmojiAndSpecialCharacters;
        _blockedKeywords = blockedKeywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(keyword => keyword.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _blockedCommenters = blockedCommenters
            .Where(commenter => !string.IsNullOrWhiteSpace(commenter))
            .Select(commenter => commenter.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled =>
        _removeEmojiAndSpecialCharacters || _blockedKeywords.Length > 0 || _blockedCommenters.Count > 0;

    internal bool IsExplicitlyExcluded(
        string content,
        string commenter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_blockedCommenters.Contains(commenter))
        {
            return true;
        }

        var contentForKeywordMatch = _removeEmojiAndSpecialCharacters
            ? RemoveEmojiAndSpecialCharacters(content)
            : content;
        return ContainsBlockedKeyword(contentForKeywordMatch, cancellationToken);
    }

    public override IReadOnlyList<Danmaku> DoFilter(IReadOnlyList<Danmaku> danmakus)
    {
        return DoFilter(danmakus, CancellationToken.None);
    }

    public override IReadOnlyList<Danmaku> DoFilter(
        IReadOnlyList<Danmaku> danmakus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(danmakus);

        if (!IsEnabled)
        {
            return danmakus;
        }

        var kept = new List<Danmaku>(danmakus.Count);
        foreach (var danmaku in danmakus)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_blockedCommenters.Contains(danmaku.Commenter))
            {
                continue;
            }

            var content = _removeEmojiAndSpecialCharacters
                ? RemoveEmojiAndSpecialCharacters(danmaku.Content)
                : danmaku.Content;
            if ((_removeEmojiAndSpecialCharacters && string.IsNullOrWhiteSpace(content))
                || ContainsBlockedKeyword(content, cancellationToken))
            {
                continue;
            }

            kept.Add(content == danmaku.Content ? danmaku : CopyWithContent(danmaku, content));
        }

        return kept;
    }

    private bool ContainsBlockedKeyword(string content, CancellationToken cancellationToken)
    {
        foreach (var keyword in _blockedKeywords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Danmaku CopyWithContent(Danmaku danmaku, string content)
    {
        return new Danmaku
        {
            Start = danmaku.Start,
            Style = danmaku.Style,
            Color = danmaku.Color,
            Commenter = danmaku.Commenter,
            Content = content,
            SizeRatio = danmaku.SizeRatio
        };
    }

    private static string RemoveEmojiAndSpecialCharacters(string content)
    {
        StringBuilder? cleaned = null;
        var elements = StringInfo.GetTextElementEnumerator(content);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (IsEmojiOrSpecialElement(element))
            {
                cleaned ??= new StringBuilder(content.Length)
                    .Append(content.AsSpan(0, elements.ElementIndex));
                continue;
            }

            cleaned?.Append(element);
        }

        return cleaned?.ToString() ?? content;
    }

    private static bool IsEmojiOrSpecialElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            if (rune.Value is 0x20E3 or 0xFE0F
                || Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control or
                    UnicodeCategory.Format or
                    UnicodeCategory.ModifierSymbol or
                    UnicodeCategory.OtherSymbol or
                    UnicodeCategory.PrivateUse or
                    UnicodeCategory.OtherNotAssigned)
            {
                return true;
            }
        }

        return false;
    }
}
