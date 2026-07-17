using System;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal static class ImageSourceUriResolver
{
    public static Uri? ResolveExternal(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var candidate = source.StartsWith("//", StringComparison.Ordinal)
            ? $"{Uri.UriSchemeHttps}:{source}"
            : source;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }
}
