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
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<DownloadTaskId, string> _directories = new();
    private readonly ConcurrentDictionary<string, FileStream> _sessionLocks = new();
    private readonly ILogger<DownloadTaskStaging> _logger;

    public DownloadTaskStaging(ILogger<DownloadTaskStaging> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string GetDirectory(DownloadTaskId taskId, string outputBasePath, string stagingToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        if (!Guid.TryParseExact(stagingToken, "N", out _))
        {
            throw new ArgumentException("Staging token must be a GUID.", nameof(stagingToken));
        }

        var root = GetRoot(outputBasePath);
        var sessionDirectory = Path.Combine(root, stagingToken);
        var directory = Path.Combine(sessionDirectory, "data");
        lock (_gate)
        {
            if (_directories.TryGetValue(taskId, out var existing) &&
                !string.Equals(existing, directory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A download task's output directory changed.");
            }

            EnsureOrdinaryStagingPath(root, directory);
            if (!_sessionLocks.ContainsKey(sessionDirectory))
            {
                if (Directory.Exists(sessionDirectory) &&
                    !File.Exists(Path.Combine(sessionDirectory, ".session-lock")))
                {
                    throw new IOException("Existing staging has no task ownership marker.");
                }

                Directory.CreateDirectory(sessionDirectory);
                EnsureOrdinaryStagingPath(root, sessionDirectory);
                _sessionLocks[sessionDirectory] = new FileStream(
                    Path.Combine(sessionDirectory, ".session-lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }

            Directory.CreateDirectory(directory);
            EnsureOrdinaryStagingPath(root, directory);
            _directories[taskId] = directory;
            return directory;
        }
    }

    public bool CleanupTask(DownloadTaskId taskId)
    {
        lock (_gate)
        {
            if (!_directories.TryGetValue(taskId, out var directory))
            {
                return true;
            }

            var sessionDirectory = Path.GetDirectoryName(directory)!;
            if (_sessionLocks.TryRemove(sessionDirectory, out var sessionLock))
            {
                sessionLock.Dispose();
            }

            var succeeded = TryDelete(sessionDirectory, Path.GetDirectoryName(sessionDirectory)!);
            if (succeeded)
            {
                _directories.TryRemove(taskId, out _);
            }

            return succeeded;
        }
    }

    public bool CleanupTask(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var root = GetRoot(task.Output.BasePath);
        var sessionDirectory = Path.Combine(root, task.Output.StagingToken);
        lock (_gate)
        {
            if (_directories.TryGetValue(task.Id, out var knownDirectory))
            {
                if (!string.Equals(Path.GetDirectoryName(knownDirectory), sessionDirectory,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A download task's staging identity changed.");
                }

                return CleanupTask(task.Id);
            }

            if (!Directory.Exists(sessionDirectory))
            {
                return true;
            }

            try
            {
                EnsureOrdinaryStagingPath(root, sessionDirectory);
                using (new FileStream(Path.Combine(sessionDirectory, ".session-lock"),
                           FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                return TryDelete(sessionDirectory, root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarningMessage("Task-private staging cleanup could not complete.", exception);
                return false;
            }
        }
    }

    public void CleanupStale(IEnumerable<DownloadTask> tasks)
    {
        foreach (var task in tasks.Where(task => task.Phase == DownloadPhase.Completed))
        {
            var root = GetRoot(task.Output.BasePath);
            var sessionDirectory = Path.Combine(root, task.Output.StagingToken);
            if (!Directory.Exists(sessionDirectory))
            {
                continue;
            }

            try
            {
                EnsureOrdinaryStagingPath(root, sessionDirectory);
                using (new FileStream(Path.Combine(sessionDirectory, ".session-lock"),
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

    public void CleanupCurrentSession()
    {
        lock (_gate)
        {
            foreach (var entry in _sessionLocks)
            {
                entry.Value.Dispose();
            }

            _sessionLocks.Clear();
            _directories.Clear();
        }
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

    private bool TryDelete(string directory, string root)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                EnsureOrdinaryStagingPath(root, directory);
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarningMessage("Task-private staging cleanup could not complete.", exception);
            return false;
        }
    }
}
