using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadOutputPathResolver
{
    public static async Task<string> ResolveAdmissionCollisionAsync(
        string basePath,
        bool autoAddNumberSuffix,
        Func<string, CancellationToken, Task<bool>> hasOutputClaimConflictAsync,
        Func<CancellationToken, Task<IReadOnlyList<string>>> getReservationKeysAsync,
        CancellationToken cancellationToken,
        Func<string, bool>? hasExistingOutputClaimConflict = null,
        StringComparer? comparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(hasOutputClaimConflictAsync);
        ArgumentNullException.ThrowIfNull(getReservationKeysAsync);
        cancellationToken.ThrowIfCancellationRequested();
        comparer ??= PlatformComparer;
        hasExistingOutputClaimConflict ??= static _ => false;
        var occupiedPaths = GetExistingBasePaths(basePath)
            .Select(CreateComparisonKey)
            .ToHashSet(comparer);
        var baseKey = CreateComparisonKey(basePath);
        var baseHasOutputClaimConflict = hasExistingOutputClaimConflict(basePath);
        if (baseHasOutputClaimConflict)
        {
            occupiedPaths.Add(baseKey);
        }
        if (!occupiedPaths.Contains(baseKey)
            && !baseHasOutputClaimConflict &&
            !await hasOutputClaimConflictAsync(basePath, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return basePath;
        }

        if (!autoAddNumberSuffix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new DownloadOutputCollisionException();
        }

        occupiedPaths.UnionWith(await getReservationKeysAsync(cancellationToken).ConfigureAwait(false));
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? basePath : $"{basePath}({suffix})";
            var comparisonKey = CreateComparisonKey(candidate);
            if (!occupiedPaths.Contains(comparisonKey)
                && !hasExistingOutputClaimConflict(candidate))
            {
                return candidate;
            }
        }
    }

    internal static StringComparer PlatformComparer =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    internal static bool HasExistingOutputClaimConflict(
        string basePath,
        DownloadActionClaims requestedClaims)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var directory = Path.GetDirectoryName(basePath);
        if (directory == null || !Directory.Exists(directory))
        {
            return false;
        }

        return Directory.EnumerateFiles(directory).Any(file =>
            DownloadOutputLayout.HasOutputClaimConflict(basePath, file, requestedClaims));
    }

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
