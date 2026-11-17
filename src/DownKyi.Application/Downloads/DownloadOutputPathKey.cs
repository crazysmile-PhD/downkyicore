using System.Text;

namespace DownKyi.Application.Downloads;

public static class DownloadOutputPathKey
{
    public static bool UsesCaseInsensitiveComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static string Create(string basePath, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var normalized = Path
            .TrimEndingDirectorySeparator(Path.GetFullPath(basePath))
            .Normalize(NormalizationForm.FormC);
        return ignoreCase ? normalized.ToUpperInvariant() : normalized;
    }

    public static bool IsSameOrDescendant(string basePath, string directory, bool ignoreCase)
    {
        var key = Create(basePath, ignoreCase);
        var directoryKey = Create(directory, ignoreCase);
        var prefix = Path.EndsInDirectorySeparator(directoryKey)
            ? directoryKey : directoryKey + Path.DirectorySeparatorChar;
        return string.Equals(key, directoryKey, StringComparison.Ordinal)
               || key.StartsWith(prefix, StringComparison.Ordinal);
    }
}
