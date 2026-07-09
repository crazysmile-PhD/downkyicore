namespace DownKyi.Core.Storage;

public static class StorageManager
{
    private const long MaxLogBytes = 64L * 1024 * 1024;
    private const long MaxCacheBytes = 512L * 1024 * 1024;
    private const long MaxAriaLogBytes = 32L * 1024 * 1024;

    public static string GetRoot()
    {
        return CreateDirectory(Constant.Root);
    }

    public static bool IsPortableMode()
    {
        return Constant.IsPortableMode;
    }

    /// <summary>
    /// 获取Aria的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetAriaDir()
    {
        CreateDirectory(Constant.Aria);
        return Constant.Aria;
    }

    /// <summary>
    /// 获取日志的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetLogsDir()
    {
        CreateDirectory(Constant.Logs);
        return Constant.Logs;
    }

    /// <summary>
    /// 获取历史记录的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetDownload()
    {
        CreateDirectory(Constant.Database);
        return Constant.Download;
    }

    /// <summary>
    /// 获取历史记录的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetDbPath()
    {
        CreateDirectory(Constant.Database);
        return Constant.DbPath;
    }

    /// <summary>
    /// 获取设置的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetSettings()
    {
        CreateDirectory(Constant.Config);
        return Constant.Settings;
    }

    /// <summary>
    /// 获取登录cookies的文件路径
    /// </summary>
    /// <returns></returns>
    public static string GetLogin()
    {
        CreateDirectory(Constant.Config);
        return Constant.Login;
    }

    /// <summary>
    /// 获取弹幕的文件夹路径
    /// </summary>
    /// <returns></returns>
    public static string GetDanmaku()
    {
        return CreateDirectory(Constant.Danmaku);
    }

    public static string GetMedia()
    {
        return CreateDirectory(Constant.Media);
    }

    public static string GetCache()
    {
        return CreateDirectory(Constant.Cache);
    }

    public static Task RunMaintenanceAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunMaintenance(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 若文件夹不存在，则创建文件夹
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    private static string CreateDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return directory;
    }

    private static void RunMaintenance(CancellationToken cancellationToken)
    {
        CleanupDirectory(Constant.Logs, TimeSpan.FromDays(30), MaxLogBytes, "*", cancellationToken);
        CleanupDirectory(Constant.Cache, TimeSpan.FromDays(14), MaxCacheBytes, "*", cancellationToken);
        CleanupDirectory(Constant.Aria, TimeSpan.FromDays(14), MaxAriaLogBytes, "*.log", cancellationToken);
        CleanupTemporaryFiles(Constant.Database, TimeSpan.FromDays(3), cancellationToken);
        DeleteEmptyDirectories(Constant.Cache, cancellationToken);
    }

    private static void CleanupDirectory(
        string directory,
        TimeSpan maxAge,
        long maxTotalBytes,
        string pattern,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || !IsUnderRoot(directory))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var files = Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && IsUnderRoot(file.FullName))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        foreach (var file in files.Where(file => now - file.LastWriteTimeUtc > maxAge))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDelete(file.FullName);
        }

        files = Directory
            .EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && IsUnderRoot(file.FullName))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        var totalBytes = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes <= maxTotalBytes)
            {
                break;
            }

            totalBytes -= file.Length;
            TryDelete(file.FullName);
        }
    }

    private static void CleanupTemporaryFiles(string directory, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || !IsUnderRoot(directory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tmp", ".bak", ".old" };
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Exists
                && file.LastWriteTimeUtc < cutoff
                && extensions.Contains(file.Extension)
                && IsUnderRoot(file.FullName))
            {
                TryDelete(file.FullName);
            }
        }
    }

    private static void DeleteEmptyDirectories(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || !IsUnderRoot(directory))
        {
            return;
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderRoot(childDirectory))
            {
                continue;
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(childDirectory).Any())
                {
                    Directory.Delete(childDirectory);
                }
            }
            catch
            {
                // Best-effort maintenance.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort maintenance.
        }
    }

    private static bool IsUnderRoot(string path)
    {
        var root = Path.GetFullPath(Constant.Root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return fullPath.StartsWith(root, comparison)
               || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   comparison);
    }
}
