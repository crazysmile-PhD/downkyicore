using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskStaging
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private readonly object _gate = new();
    private readonly Dictionary<DownloadTaskId, StagingPaths> _tasks = new();
    private readonly Dictionary<string, FileStream> _sessionLocks = new(PathComparer);
    private readonly ILogger<DownloadTaskStaging> _logger;

    public DownloadTaskStaging(ILogger<DownloadTaskStaging> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public string GetDirectory(DownloadTaskId taskId, string outputBasePath, string stagingToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBasePath);
        var paths = CreatePaths(outputBasePath, stagingToken);
        lock (_gate)
        {
            EnsureSameTaskDirectory(
                _tasks.GetValueOrDefault(taskId, paths),
                paths,
                "A download task's output directory changed.");

            EnsureOrdinaryStagingPath(paths.RootDirectory, paths.DataDirectory);
            if (!_sessionLocks.ContainsKey(paths.TaskDirectory))
            {
                if (Directory.Exists(paths.TaskDirectory) && !File.Exists(paths.LockFile))
                {
                    throw new IOException("Existing staging has no task ownership marker.");
                }

                Directory.CreateDirectory(paths.TaskDirectory);
                EnsureOrdinaryStagingPath(paths.RootDirectory, paths.TaskDirectory);
                _sessionLocks[paths.TaskDirectory] = new FileStream(
                    paths.LockFile,
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }

            Directory.CreateDirectory(paths.DataDirectory);
            EnsureOrdinaryStagingPath(paths.RootDirectory, paths.DataDirectory);
            _tasks[taskId] = paths;
            return paths.DataDirectory;
        }
    }

    public bool CleanupTask(DownloadTaskId taskId)
    {
        lock (_gate)
        {
            return !_tasks.TryGetValue(taskId, out var paths) || CleanupTask(taskId, paths);
        }
    }

    public bool CleanupTask(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var paths = CreatePaths(task.Output.BasePath, task.Output.StagingToken);
        lock (_gate)
        {
            var knownPaths = _tasks.GetValueOrDefault(task.Id, paths);
            EnsureSameTaskDirectory(
                knownPaths,
                paths,
                "A download task's staging identity changed.");

            return CleanupTask(task.Id, knownPaths);
        }
    }

    public void CleanupCurrentSession()
    {
        lock (_gate)
        {
            foreach (var sessionLock in _sessionLocks.Values)
            {
                sessionLock.Dispose();
            }

            _sessionLocks.Clear();
            _tasks.Clear();
        }
    }

    private bool CleanupTask(DownloadTaskId taskId, StagingPaths paths)
    {
        if (!TryDeleteTaskDirectory(paths))
        {
            return false;
        }

        _tasks.Remove(taskId);
        TryDeleteEmptyDirectory(paths.RootDirectory);
        TryDeleteEmptyDirectory(paths.OwnerDirectory);
        return true;
    }

    private bool TryDeleteTaskDirectory(StagingPaths paths)
    {
        try
        {
            _sessionLocks.Remove(paths.TaskDirectory, out var sessionLock);
            sessionLock?.Dispose();
            if (Directory.Exists(paths.TaskDirectory))
            {
                EnsureOrdinaryStagingPath(paths.RootDirectory, paths.TaskDirectory);
                using var ownership = new FileStream(
                    paths.LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            DeleteDirectory(paths.TaskDirectory, paths.RootDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarningMessage("Task-private staging cleanup could not complete.", exception);
            return false;
        }
    }

    private static void EnsureSameTaskDirectory(
        StagingPaths actual,
        StagingPaths expected,
        string message)
    {
        if (!PathComparer.Equals(actual.TaskDirectory, expected.TaskDirectory))
        {
            throw new InvalidOperationException(message);
        }
    }

    private void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return;
            }

            Directory.Delete(directory, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebugMessage("Empty staging parent cleanup could not complete.");
        }
    }

    private static StagingPaths CreatePaths(string outputBasePath, string stagingToken)
    {
        if (!Guid.TryParseExact(stagingToken, "N", out _))
        {
            throw new ArgumentException("Staging token must be a GUID.", nameof(stagingToken));
        }

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputBasePath))
            ?? throw new ArgumentException(
                "Output base path must have a directory.", nameof(outputBasePath));
        var ownerDirectory = Path.Combine(outputDirectory, ".downkyi");
        var rootDirectory = Path.Combine(ownerDirectory, "staging");
        var taskDirectory = Path.Combine(rootDirectory, stagingToken);
        return new StagingPaths(
            ownerDirectory,
            rootDirectory,
            taskDirectory,
            Path.Combine(taskDirectory, "data"),
            Path.Combine(taskDirectory, ".session-lock"));
    }

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

    private static void DeleteDirectory(string directory, string root)
    {
        if (Directory.Exists(directory))
        {
            EnsureOrdinaryStagingPath(root, directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record StagingPaths(
        string OwnerDirectory,
        string RootDirectory,
        string TaskDirectory,
        string DataDirectory,
        string LockFile);
}
