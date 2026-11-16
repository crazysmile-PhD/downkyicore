namespace DownKyi.Core.BiliApi.Sign;

public sealed record WbiKeys(string ImgKey, string SubKey)
{
    public bool IsValid => IsValidKey(ImgKey) && IsValidKey(SubKey);

    private static bool IsValidKey(string? value)
    {
        return value is { Length: 32 } && value.All(char.IsAsciiLetterOrDigit);
    }
}
