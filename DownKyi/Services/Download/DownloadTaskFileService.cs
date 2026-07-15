using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Logging;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskFileService
{
    private static readonly string[] MediaExtensions = { ".mp4", ".aac", ".mp3", ".flac" };
    private static readonly string[] TextExtensions = { ".ass", ".srt", ".nfo" };
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
    private static readonly string[] TempExtensions = { "", ".aria2", ".download" };
    private readonly ILogger<DownloadTaskFileService> _logger;

    public DownloadTaskFileService(ILogger<DownloadTaskFileService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CancelActiveDownloadAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        downloading.Downloading.DownloadStatus = DownloadStatus.Pause;

        try
        {
            downloading.DownloadService?.CancelAsync();
        }
        catch (InvalidOperationException e)
        {
            _logger.LogDebugMessage($"Cancel built-in downloader failed: {e.Message}");
        }
        finally
        {
            downloading.DownloadService = null;
        }

        var gid = downloading.Downloading.Gid;
        if (string.IsNullOrWhiteSpace(gid))
        {
            return;
        }

        var removed = false;
        try
        {
            await AriaClient.RemoveAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await AriaClient.RemoveDownloadResultAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            removed = true;
        }
        catch (TimeoutException e)
        {
            _logger.LogDebugMessage($"Cancel aria downloader failed: {e.Message}");
        }
        catch (HttpRequestException e)
        {
            _logger.LogDebugMessage($"Cancel aria downloader failed: {e.Message}");
        }
        finally
        {
            if (removed)
            {
                downloading.Downloading.Gid = null;
            }
        }
    }

    public Task<DownloadFileDeletionResult> DeleteGeneratedFilesAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken = default)
    {
        var files = GetGeneratedFiles(downloading);
        return DeleteFilesAsync(files, cancellationToken);
    }

    internal Task<DownloadFileDeletionResult> DeleteFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        // File.Delete has no async API and can block on network drives or antivirus scans.
        return Task.Run(() => DeleteFilesCoreAsync(files, cancellationToken), cancellationToken);
    }

    private async Task<DownloadFileDeletionResult> DeleteFilesCoreAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken)
    {
        var attemptedCount = 0;
        var failedCount = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedCount++;
            if (!await TryDeleteFileAsync(file, cancellationToken).ConfigureAwait(false))
            {
                failedCount++;
            }
        }

        return new DownloadFileDeletionResult(attemptedCount, failedCount);
    }

    public IReadOnlyCollection<string> GetGeneratedFiles(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

        return GetGeneratedFiles(
            downloading.DownloadBase?.FilePath,
            downloading.Downloading.DownloadFiles?.Values);
    }

    internal IReadOnlyCollection<string> GetGeneratedFiles(
        string? filePath,
        IEnumerable<string>? downloadFiles)
    {
        var files = OperatingSystem.IsWindows()
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.Ordinal);

        var basePath = NormalizePath(filePath);
        var directory = Path.GetDirectoryName(basePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            foreach (var fileName in downloadFiles ?? Enumerable.Empty<string>())
            {
                AddWithTempFiles(files, ResolveDownloadFile(directory, fileName));
            }
        }

        AddKnownOutputFiles(files, basePath);
        AddSubtitleVariants(files, basePath);

        return files
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static void AddKnownOutputFiles(ISet<string> files, string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return;
        }

        foreach (var extension in MediaExtensions.Concat(TextExtensions))
        {
            AddWithTempFiles(files, basePath + extension);
        }

        foreach (var extension in ImageExtensions)
        {
            AddWithTempFiles(files, basePath + extension);
            AddWithTempFiles(files, basePath + ".Cover" + extension);
        }
    }

    private void AddSubtitleVariants(ISet<string> files, string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        var name = Path.GetFileName(basePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var subtitle in Directory.EnumerateFiles(directory, $"{name}_*.srt", SearchOption.TopDirectoryOnly))
            {
                AddWithTempFiles(files, subtitle);
            }
        }
        catch (IOException e)
        {
            _logger.LogDebugMessage($"Enumerate subtitle variants failed: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogDebugMessage($"Enumerate subtitle variants was denied: {e.Message}");
        }
    }

    private static void AddWithTempFiles(ISet<string> files, string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        foreach (var extension in TempExtensions)
        {
            files.Add(file + extension);
        }
    }

    private static string ResolveDownloadFile(string directory, string fileName)
    {
        var normalized = NormalizePath(fileName);
        return Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(directory, normalized);
    }

    private static string NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private async Task<bool> TryDeleteFileAsync(string file, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return true;
            }
            catch (IOException e) when (attempt < 4)
            {
                _logger.LogDebugMessage($"Delete generated file retry {attempt + 1}: {e.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException e) when (attempt < 4)
            {
                _logger.LogDebugMessage($"Delete generated file retry {attempt + 1}: {e.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                _logger.LogErrorMessage("Generated file deletion failed.", e);
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                _logger.LogErrorMessage("Generated file deletion was denied.", e);
                return false;
            }
        }

        return false;
    }
}

internal readonly record struct DownloadFileDeletionResult(int AttemptedCount, int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
