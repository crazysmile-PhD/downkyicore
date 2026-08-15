using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadOutputPathResolver
{
    public static async Task<string> ResolveAdmissionCollisionAsync(
        string basePath,
        bool autoAddNumberSuffix,
        Func<string, CancellationToken, Task<bool>> isReservedAsync,
        CancellationToken cancellationToken,
        StringComparer? comparer = null,
        int initialSuffix = 0,
        IReadOnlySet<string>? occupiedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(isReservedAsync);
        comparer ??= PlatformComparer;
        ArgumentOutOfRangeException.ThrowIfNegative(initialSuffix);
        occupiedPaths ??= GetExistingBasePaths(basePath).ToHashSet(comparer);
        for (var suffix = initialSuffix; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? basePath : $"{basePath}({suffix})";
            var normalizedCandidate = Normalize(candidate);
            if (!occupiedPaths.Contains(normalizedCandidate) &&
                !await isReservedAsync(normalizedCandidate, cancellationToken).ConfigureAwait(false))
            {
                return normalizedCandidate;
            }

            if (!autoAddNumberSuffix)
            {
                throw new IOException("The selected output path is already in use.");
            }
        }
    }

    internal static StringComparer PlatformComparer =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static IReadOnlySet<string> CaptureExistingBasePaths(
        IEnumerable<string> basePaths,
        StringComparer? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(basePaths);
        comparer ??= PlatformComparer;

        var directories =
            new HashSet<string>(comparer);

        foreach (var basePath in basePaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(basePath);

            var normalizedBasePath =
                Normalize(basePath);

            var directory =
                Path.GetDirectoryName(normalizedBasePath);

            if (!string.IsNullOrEmpty(directory))
            {
                directories.Add(directory);
            }
        }

        var occupiedPaths =
            new HashSet<string>(comparer);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in
                     Directory.EnumerateFiles(directory))
            {
                occupiedPaths.Add(
                    Normalize(
                        Path.Combine(
                            directory,
                            Path.GetFileNameWithoutExtension(file))));
            }
        }

        return occupiedPaths;
    }

    private static string[] GetExistingBasePaths(string basePath)
    {
        var normalizedBasePath = Normalize(basePath);
        var directory = Path.GetDirectoryName(normalizedBasePath);
        if (directory == null || !Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory)
            .Select(file => Path.Combine(directory, Path.GetFileNameWithoutExtension(file)))
            .Select(Normalize)
            .ToArray();
    }

    private static string Normalize(string path)
    {
        return DownloadOutputPathKey.NormalizeLogicalPath(path);
    }
}
