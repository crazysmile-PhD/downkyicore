namespace DownKyi.Application.Downloads;

public static class DownloadOutputPathKey
{
    private static readonly FileSystemOutputIdentityProvider IdentityProvider =
        FileSystemOutputIdentityProvider.Instance;

    public static bool UsesCaseInsensitiveComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static string NormalizeLogicalPath(
        string basePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                basePath));
    }

    public static string Create(
        string basePath,
        bool ignoreCase)
    {
        return IdentityProvider.CreateReservationKey(
            basePath,
            ignoreCase);
    }
}
