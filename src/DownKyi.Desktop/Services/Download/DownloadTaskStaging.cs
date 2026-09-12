using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskStaging
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<DownloadTaskId, string> _directories = new();
    private readonly ConcurrentDictionary<string, FileStream> _sessionLocks = new();
    private readonly ILogger<DownloadTaskStaging> _logger;

    public DownloadTaskStaging(ILogger<DownloadTaskStaging> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string GetDirectory(DownloadTaskId taskId, string outputBasePath)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        var root = GetRoot(outputBasePath);
        var sessionDirectory = Path.Combine(root, _session);
        EnsureOrdinaryStagingPath(root, sessionDirectory);
        _sessionLocks.GetOrAdd(root, path =>
        {
            Directory.CreateDirectory(Path.Combine(path, _session));
            EnsureOrdinaryStagingPath(path, Path.Combine(path, _session));
            return new FileStream(
                Path.Combine(path, _session, ".session-lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        });
        var directory = _directories.GetOrAdd(taskId, _ =>
            Path.Combine(root, _session, Guid.NewGuid().ToString("N")));
        if (!Path.GetFullPath(directory).StartsWith(
                Path.GetFullPath(Path.Combine(root, _session)) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A download task's output directory changed.");
        }

        return directory;
    }

    public void CleanupTask(DownloadTaskId taskId)
    {
        if (_directories.TryRemove(taskId, out var directory))
        {
            TryDelete(directory, Path.GetDirectoryName(Path.GetDirectoryName(directory))!);
        }
    }

    public void CleanupStale(IEnumerable<DownloadTask> tasks)
    {
        foreach (var root in tasks.Select(task => GetRoot(task.Output.BasePath)).Distinct(
                     OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                EnsureOrdinaryStagingPath(root, root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarningMessage("Staging root is redirected or unavailable.", exception);
                continue;
            }

            foreach (var sessionDirectory in Directory.EnumerateDirectories(root))
            {
                var sessionName = Path.GetFileName(sessionDirectory);
                var lockPath = Path.Combine(sessionDirectory, ".session-lock");
                if (sessionName != _session && Guid.TryParseExact(sessionName, "N", out _))
                {
                    try
                    {
                        if ((File.GetAttributes(sessionDirectory) & FileAttributes.ReparsePoint) != 0 ||
                            !File.Exists(lockPath))
                        {
                            continue;
                        }

                        using (new FileStream(
                                   lockPath,
                                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                        }

                        TryDelete(sessionDirectory, root);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarningMessage("Stale staging is still in use or unavailable.", exception);
                    }
                }
            }
        }
    }

    public void CleanupCurrentSession()
    {
        foreach (var directory in _directories.Values.Distinct())
        {
            TryDelete(directory, Path.GetDirectoryName(Path.GetDirectoryName(directory))!);
        }

        _directories.Clear();
        foreach (var entry in _sessionLocks)
        {
            entry.Value.Dispose();
            TryDelete(Path.Combine(entry.Key, _session), entry.Key);
        }

        _sessionLocks.Clear();
    }

    private static string GetRoot(string outputBasePath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(outputBasePath))
            ?? throw new ArgumentException("Output base path must have a directory.", nameof(outputBasePath)),
            ".downkyi", "staging");

    private static void EnsureOrdinaryStagingPath(string root, string target)
    {
        foreach (var path in new[]
                 {
                     Path.GetDirectoryName(root)!, root,
                     Path.GetDirectoryName(target)!, target
                 }.Distinct(OperatingSystem.IsWindows()
                     ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            if (Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Task-private staging contains a redirected directory.");
            }
        }
    }

    private void TryDelete(string directory, string root)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                EnsureOrdinaryStagingPath(root, directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarningMessage("Task-private staging cleanup could not complete.", exception);
        }
    }
}
