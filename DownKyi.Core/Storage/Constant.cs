namespace DownKyi.Core.Storage;

/// <summary>
/// 存储到本地时使用的一些常量
/// </summary>
internal static class Constant
{
    private const string AppDirectoryName = "DownKyi";
    private static readonly string LegacyRoot = Path.GetFullPath(AppContext.BaseDirectory);
    public static bool IsPortableMode { get; } = ResolvePortableMode();
    public static string Root { get; } = ResolveRoot();

    static Constant()
    {
        if (!IsPortableMode && !PathsEqual(LegacyRoot, Root))
        {
            MigrateLegacyData();
        }
    }

    private static string ResolveRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("DOWNKYI_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot);
        }

        if (IsPortableMode)
        {
            return LegacyRoot;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                AppDirectoryName);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDirectoryName);
    }

    private static bool ResolvePortableMode()
    {
        var portable = Environment.GetEnvironmentVariable("DOWNKYI_PORTABLE");
        if (!string.IsNullOrWhiteSpace(portable))
        {
            return portable.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || portable.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || portable.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        return File.Exists(Path.Combine(LegacyRoot, "portable"))
               || File.Exists(Path.Combine(LegacyRoot, ".portable"))
               || File.Exists(Path.Combine(LegacyRoot, "DownKyi.portable"));
    }

    private static void MigrateLegacyData()
    {
        foreach (var directoryName in new[] { "Aria", "Logs", "Storage", "Config", "Bilibili", "Cache" })
        {
            var source = Path.Combine(LegacyRoot, directoryName);
            var target = Path.Combine(Root, directoryName);
            CopyDirectoryIfMissing(source, target);
        }
    }

    private static void CopyDirectoryIfMissing(string source, string target)
    {
        if (!Directory.Exists(source) || PathsEqual(source, target))
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, sourceFile);
            var targetFile = Path.Combine(target, relativePath);
            if (File.Exists(targetFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    // Aria
    public static string Aria { get; } = Path.Combine(Root, "Aria");

    // 日志
    public static string Logs { get; } = Path.Combine(Root, "Logs");

    // 数据库
    public static string Database { get; } = Path.Combine(Root, "Storage");

    // 历史(搜索、下载) (加密)
    public static string Download { get; } = Path.Combine(Database, "Download.db");

    public static string DbPath { get; } = Path.Combine(Database, "Data.db");

    // 配置
    public static string Config { get; } = Path.Combine(Root, "Config");

    // 设置
    public static string Settings { get; } = Path.Combine(Config, "Settings");

    // 登录cookies
    public static string Login { get; } = Path.Combine(Config, "Login");

    // Bilibili
    private static string Bilibili { get; } = Path.Combine(Root, "Bilibili");

    // 弹幕
    public static string Danmaku { get; } = Path.Combine(Bilibili, "Danmakus");

    // 下载
    public static string Media { get; } = Path.Combine(Root, "Media");

    // 缓存
    public static string Cache { get; } = Path.Combine(Root, "Cache");
}
