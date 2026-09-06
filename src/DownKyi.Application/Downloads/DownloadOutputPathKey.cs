using System.Text;

namespace DownKyi.Application.Downloads;

public static class DownloadOutputPathKey
{
    public static bool UsesCaseInsensitiveComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static string NormalizeLogicalPath(string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        return Path
            .TrimEndingDirectorySeparator(Path.GetFullPath(basePath))
            .Normalize(NormalizationForm.FormC);
    }

    public static string Create(string basePath, bool ignoreCase)
    {
        var normalized = NormalizeLogicalPath(basePath);
        return ignoreCase ? normalized.ToUpperInvariant() : normalized;
    }
}
