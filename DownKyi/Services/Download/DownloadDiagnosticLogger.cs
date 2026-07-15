using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Core.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadDiagnosticLogger
{
    private static readonly TimeSpan SpeedLogInterval = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSpeedLogTimes = new();
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<DownloadDiagnosticLogger> _logger;

    public DownloadDiagnosticLogger(
        ISettingsStore settingsStore,
        ILogger<DownloadDiagnosticLogger> logger)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void LogAriaServerConfig(string source, AriaConfig config)
    {
        _logger.LogInformationMessage(
            $"source={source}; Aria config " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"maxTasks={config.MaxConcurrentDownloads}; " +
            $"split={config.Split}; " +
            $"maxConnectionPerServer={config.MaxConnectionPerServer}; " +
            $"minSplitSize={config.MinSplitSize}MB; " +
            $"globalLimit={FormatLimit(config.MaxOverallDownloadLimit)}; " +
            $"taskLimit={FormatLimit(config.MaxDownloadLimit)}; " +
            $"fileAllocation={config.FileAllocation}");
    }

    public void LogAriaTaskStart(string source, string? gid, int urlCount)
    {
        var settings = _settingsStore.Current.Network;
        var taskId = ShortId(gid);
        using var scope = _logger.BeginOperationScope(taskId, taskId);
        _logger.LogInformationMessage(
            $"source={source}; Aria task started " +
            $"task={taskId}; " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"urlCount={urlCount}; " +
            $"split={settings.AriaSplit}; " +
            $"maxConnectionPerServer={settings.AriaMaxConnectionPerServer}; " +
            $"minSplitSize={settings.AriaMinSplitSize}MB; " +
            $"globalLimit={settings.AriaMaxOverallDownloadLimit}KB/s; " +
            $"taskLimit={settings.AriaMaxDownloadLimit}KB/s");
    }

    public void LogBuiltInTaskStart(string source, string taskId, int urlCount, int chunkCount, int parallelCount)
    {
        var safeTaskId = ShortId(taskId);
        using var scope = _logger.BeginOperationScope(safeTaskId, safeTaskId);
        _logger.LogInformationMessage(
            $"source={source}; Built-in task started " +
            $"task={safeTaskId}; " +
            $"highSpeed={IsHighSpeedEnabled()}; " +
            $"urlCount={urlCount}; " +
            $"chunkCount={chunkCount}; " +
            $"parallelCount={parallelCount}");
    }

    public void LogSpeed(string source, string taskId, long completedLength, long totalLength, long bytesPerSecond)
    {
        var key = $"{source}:{taskId}";
        var now = DateTimeOffset.UtcNow;

        if (_lastSpeedLogTimes.TryGetValue(key, out var last) && now - last < SpeedLogInterval)
        {
            return;
        }

        _lastSpeedLogTimes[key] = now;
        var safeTaskId = ShortId(taskId);
        using var scope = _logger.BeginOperationScope(safeTaskId, safeTaskId);
        _logger.LogInformationMessage(
            $"source={source}; Download speed " +
            $"task={safeTaskId}; " +
            $"speed={Format.FormatSpeedWithBandwidth(bytesPerSecond)}; " +
            $"progress={Format.FormatFileSize(completedLength)}/{Format.FormatFileSize(totalLength)}");
    }

    private bool IsHighSpeedEnabled()
    {
        return _settingsStore.Current.Network.HighSpeedDownloadMode == AllowStatus.Yes;
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

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 6);
    }
}
