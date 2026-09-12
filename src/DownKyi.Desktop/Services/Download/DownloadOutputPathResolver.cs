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
        Func<CancellationToken, Task<IReadOnlyList<string>>> getReservationKeysAsync,
        CancellationToken cancellationToken,
        StringComparer? comparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(isReservedAsync);
        ArgumentNullException.ThrowIfNull(getReservationKeysAsync);
        cancellationToken.ThrowIfCancellationRequested();
        comparer ??= PlatformComparer;
        var occupiedPaths = GetExistingBasePaths(basePath)
            .Select(CreateComparisonKey)
            .ToHashSet(comparer);
        var baseKey = CreateComparisonKey(basePath);
        if (!occupiedPaths.Contains(baseKey) &&
            !await isReservedAsync(basePath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return basePath;
        }

        if (!autoAddNumberSuffix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("The selected output path is already in use.");
        }

        occupiedPaths.UnionWith(await getReservationKeysAsync(cancellationToken).ConfigureAwait(false));
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? basePath : $"{basePath}({suffix})";
            var comparisonKey = CreateComparisonKey(candidate);
            if (!occupiedPaths.Contains(comparisonKey))
            {
                return candidate;
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

    private static string[] GetExistingBasePaths(string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        if (directory == null || !Directory.Exists(directory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(directory)
            .Select(file => Path.Combine(directory, Path.GetFileNameWithoutExtension(file)))
            .ToArray();
    }

    private static string CreateComparisonKey(string path)
    {
        return DownloadOutputPathKey.Create(path, ignoreCase: false);
    }
}
