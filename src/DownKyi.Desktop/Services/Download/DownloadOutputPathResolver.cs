using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        bool allowExistingBasePath = false,
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
        if ((!occupiedPaths.Contains(baseKey) || allowExistingBasePath)
            && !baseHasOutputClaimConflict &&
            !await hasOutputClaimConflictAsync(basePath, cancellationToken).ConfigureAwait(false))
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
            if (!occupiedPaths.Contains(comparisonKey)
                && !hasExistingOutputClaimConflict(candidate))
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

    internal static bool HasExistingOutputClaimConflict(
        string basePath,
        DownloadContentSelection requestedContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(requestedContent);
        var directory = Path.GetDirectoryName(basePath);
        if (directory == null || !Directory.Exists(directory))
        {
            return false;
        }

        var baseName = Path.GetFileName(basePath);
        return Directory.EnumerateFiles(directory).Any(file =>
        {
            var fileName = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            if (requestedContent.HasMedia
                && PlatformComparer.Equals(stem, baseName)
                && MediaExtensions.Contains(extension))
            {
                return true;
            }

            if (requestedContent.HasSubtitleAction
                && extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)
                && (PlatformComparer.Equals(stem, baseName)
                    || stem.StartsWith(baseName + "_", PlatformComparison)))
            {
                return true;
            }

            var requestedDanmaku = requestedContent.DanmakuOutputFormat
                                    ?? AllDanmakuFormats;
            if (requestedContent.Danmaku
                && (requestedDanmaku.HasFlag(DownloadDanmakuOutputFormat.Ass)
                    && PlatformComparer.Equals(fileName, baseName + ".ass")
                    || requestedDanmaku.HasFlag(DownloadDanmakuOutputFormat.Xml)
                    && PlatformComparer.Equals(fileName, baseName + ".xml")))
            {
                return true;
            }

            return requestedContent.Cover
                   && ImageExtensions.Contains(extension)
                   && (PlatformComparer.Equals(stem, baseName)
                       || stem.StartsWith(baseName + ".Cover", PlatformComparison));
        });
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

    private static StringComparison PlatformComparison =>
        DownloadOutputPathKey.UsesCaseInsensitiveComparison
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly ImmutableHashSet<string> MediaExtensions =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ".mp4",
            ".mp3",
            ".aac",
            ".flac");

    private static readonly ImmutableHashSet<string> ImageExtensions =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            ".jpg",
            ".jpeg",
            ".png",
            ".webp",
            ".bmp",
            ".gif",
            ".avif");

    private const DownloadDanmakuOutputFormat AllDanmakuFormats =
        DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml;
}
