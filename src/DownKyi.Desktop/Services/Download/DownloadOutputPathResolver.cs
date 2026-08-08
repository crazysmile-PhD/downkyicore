using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DownKyi.Services.Download;

internal static class DownloadOutputPathResolver
{
    public static string ResolveExistingFileCollision(
        string basePath,
        StringComparer? comparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        comparer ??= PlatformComparer;
        var occupiedPaths = GetExistingBasePaths(basePath);
        return occupiedPaths.Contains(Normalize(basePath), comparer)
            ? FindAvailableSuffix(basePath, occupiedPaths, comparer)
            : basePath;
    }

    public static string ResolveActiveCollision(
        string basePath,
        IEnumerable<string> activeBasePaths,
        StringComparer? comparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(activeBasePaths);
        comparer ??= PlatformComparer;
        var normalizedBasePath = Normalize(basePath);
        var occupiedPaths = activeBasePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .ToHashSet(comparer);
        occupiedPaths.UnionWith(GetExistingBasePaths(basePath));
        if (!occupiedPaths.Contains(normalizedBasePath))
        {
            return basePath;
        }

        return FindAvailableSuffix(basePath, occupiedPaths, comparer);
    }

    internal static StringComparer PlatformComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string FindAvailableSuffix(
        string basePath,
        IEnumerable<string> occupiedPaths,
        StringComparer comparer)
    {
        var occupied = occupiedPaths.ToHashSet(comparer);
        for (var suffix = 1; ; suffix++)
        {
            var candidate = $"{basePath}({suffix})";
            if (!occupied.Contains(Normalize(candidate)))
            {
                return candidate;
            }
        }
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
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
