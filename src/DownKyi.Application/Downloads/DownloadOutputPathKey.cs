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
}
