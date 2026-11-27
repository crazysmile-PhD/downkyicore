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
        Func<string, CancellationToken, Task<bool>> hasOutputClaimConflictAsync,
        Func<CancellationToken, Task<IReadOnlyList<string>>> getReservationKeysAsync,
        CancellationToken cancellationToken,
        DownloadActionClaims requestedClaims = default,
        StringComparer? comparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(hasOutputClaimConflictAsync);
        ArgumentNullException.ThrowIfNull(getReservationKeysAsync);
        cancellationToken.ThrowIfCancellationRequested();
        comparer ??= PlatformComparer;
        var files = await Task.Run(() => GetExistingFiles(basePath, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var directory = Path.GetDirectoryName(basePath)!;
        var occupiedPaths = files.Select(file => Path.Combine(directory, Path.GetFileNameWithoutExtension(file)))
            .Select(CreateComparisonKey)
            .ToHashSet(comparer);
        var baseKey = CreateComparisonKey(basePath);
        var baseHasOutputClaimConflict = HasExistingOutputClaimConflict(basePath);
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

        occupiedPaths.UnionWith(await getReservationKeysAsync(cancellationToken).ConfigureAwait(false));
        for (var suffix = 0; ; suffix++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = suffix == 0 ? basePath : $"{basePath}({suffix})";
            var comparisonKey = CreateComparisonKey(candidate);
            if (!occupiedPaths.Contains(comparisonKey)
                && !HasExistingOutputClaimConflict(candidate))
            {
                return candidate;
            }
        }

        bool HasExistingOutputClaimConflict(string path) => requestedClaims.HasAny && files.Any(file =>
            DownloadOutputLayout.HasOutputClaimConflict(path, file, requestedClaims));
    }

    internal static StringComparer PlatformComparer =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static string[] GetExistingFiles(string basePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(basePath);
        if (directory == null || !Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(file);
        }
        return files.ToArray();
    }

    private static string CreateComparisonKey(string path)
    {
        return DownloadOutputPathKey.Create(path, ignoreCase: false);
    }

}
