using System;

namespace DownKyi.Services;

internal static class GitHubIssueUriBuilder
{
    private const int MaximumIssueUriLength = 8_000;

    public static Uri Create(string title, string body, string truncatedBodySuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(truncatedBodySuffix);

        var prefix =
            $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/issues/new" +
            $"?title={Uri.EscapeDataString(title)}&body=";
        return CreateWithPrefix(prefix, body, truncatedBodySuffix);
    }

    public static Uri CreateForm(
        string title,
        string template,
        string fieldId,
        string fieldValue,
        string truncatedFieldSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentNullException.ThrowIfNull(fieldValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(truncatedFieldSuffix);

        var prefix =
            $"https://github.com/{AppConstant.RepoOwner}/{AppConstant.RepoName}/issues/new" +
            $"?template={Uri.EscapeDataString(template)}" +
            $"&title={Uri.EscapeDataString(title)}&{Uri.EscapeDataString(fieldId)}=";
        return CreateWithPrefix(prefix, fieldValue, truncatedFieldSuffix);
    }

    private static Uri CreateWithPrefix(string prefix, string body, string truncatedBodySuffix)
    {
        var encodedBody = Uri.EscapeDataString(body);
        if (prefix.Length + encodedBody.Length <= MaximumIssueUriLength)
        {
            return new Uri(prefix + encodedBody);
        }

        var low = 0;
        var high = body.Length;
        var bestLength = 0;
        while (low <= high)
        {
            var midpoint = low + ((high - low) / 2);
            var candidateLength = GetSafePrefixLength(body, midpoint);
            var candidateBody = body[..candidateLength] + truncatedBodySuffix;
            if (prefix.Length + Uri.EscapeDataString(candidateBody).Length <= MaximumIssueUriLength)
            {
                bestLength = Math.Max(bestLength, candidateLength);
                low = midpoint + 1;
            }
            else
            {
                high = midpoint - 1;
            }
        }

        return new Uri(prefix + Uri.EscapeDataString(body[..bestLength] + truncatedBodySuffix));
    }

    private static int GetSafePrefixLength(string text, int length)
    {
        if (length > 0 && length < text.Length &&
            char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
        {
            return length - 1;
        }

        return length;
    }
}
