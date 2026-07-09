using System;
using System.Collections.Concurrent;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;

namespace DownKyi.Services.Download;

internal static class DownloadDiagnosticLogger
{
    private static readonly TimeSpan SpeedLogInterval = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSpeedLogTimes = new();

    public static void LogAriaServerConfig(string source, AriaConfig config)
    {
        LogManager.Info(source,
            "Aria config " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"maxTasks={config.MaxConcurrentDownloads}; " +
            $"split={config.Split}; " +
            $"maxConnectionPerServer={config.MaxConnectionPerServer}; " +
            $"minSplitSize={config.MinSplitSize}MB; " +
            $"globalLimit={FormatLimit(config.MaxOverallDownloadLimit)}; " +
            $"taskLimit={FormatLimit(config.MaxDownloadLimit)}; " +
            $"fileAllocation={config.FileAllocation}");
    }

    public static void LogAriaTaskStart(string source, string? gid, int urlCount)
    {
        var settings = SettingsManager.GetInstance();
        LogManager.Info(source,
            "Aria task started " +
            $"task={ShortId(gid)}; " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"urlCount={urlCount}; " +
            $"split={settings.GetAriaSplit()}; " +
            $"maxConnectionPerServer={settings.GetAriaMaxConnectionPerServer()}; " +
            $"minSplitSize={settings.GetAriaMinSplitSize()}MB; " +
            $"globalLimit={settings.GetAriaMaxOverallDownloadLimit()}KB/s; " +
            $"taskLimit={settings.GetAriaMaxDownloadLimit()}KB/s");
    }

    public static void LogBuiltInTaskStart(string source, string taskId, int urlCount, int chunkCount, int parallelCount)
    {
        LogManager.Info(source,
            "Built-in task started " +
            $"task={ShortId(taskId)}; " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"urlCount={urlCount}; " +
            $"chunkCount={chunkCount}; " +
            $"parallelCount={parallelCount}");
    }

    public static void LogSpeed(string source, string taskId, long completedLength, long totalLength, long bytesPerSecond)
    {
        var key = $"{source}:{taskId}";
        var now = DateTimeOffset.UtcNow;

        if (LastSpeedLogTimes.TryGetValue(key, out var last) && now - last < SpeedLogInterval)
        {
            return;
        }

        LastSpeedLogTimes[key] = now;
        LogManager.Info(source,
            "Download speed " +
            $"task={ShortId(taskId)}; " +
            $"speed={Format.FormatSpeedWithBandwidth(bytesPerSecond)}; " +
            $"progress={Format.FormatFileSize(completedLength)}/{Format.FormatFileSize(totalLength)}");
    }

    private static bool IsHighSpeedEnabled()
    {
        return SettingsManager.GetInstance().GetHighSpeedDownloadMode() == AllowStatus.Yes;
    }

    private static string FormatLimit(long bytesPerSecond)
    {
        return bytesPerSecond <= 0 ? "unlimited" : Format.FormatSpeedWithBandwidth(bytesPerSecond);
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Length <= 8 ? value : value[..8];
    }
}
