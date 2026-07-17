namespace DownKyi.Application.Media;

public static class VideoInputResolver
{
    public static VideoInputKind Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return VideoInputKind.Unknown;
        }

        var value = input.Trim();
        if (ContainsPath(value, "/cheese/play/") || StartsWithId(value, "cheese"))
        {
            return VideoInputKind.Cheese;
        }

        if (StartsWithId(value, "ss") ||
            StartsWithId(value, "ep") ||
            StartsWithId(value, "md") ||
            ContainsPath(value, "/bangumi/play/"))
        {
            return VideoInputKind.Bangumi;
        }

        if (StartsWithId(value, "av") ||
            value.StartsWith("BV", StringComparison.OrdinalIgnoreCase) ||
            ContainsPath(value, "/video/"))
        {
            return VideoInputKind.Video;
        }

        return VideoInputKind.Unknown;
    }

    private static bool ContainsPath(string input, string path)
    {
        return input.Contains(path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithId(string input, string prefix)
    {
        if (!input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || input.Length == prefix.Length)
        {
            return false;
        }

        return input.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
