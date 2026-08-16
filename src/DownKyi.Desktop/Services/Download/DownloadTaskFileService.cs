using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskFileService
{
    private static readonly string[] MediaExtensions = { ".mp4", ".aac", ".mp3", ".flac" };
    private static readonly string[] TextExtensions = { ".ass", ".srt", ".nfo" };
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif" };
    private static readonly string[] TempExtensions = { "", ".aria2", ".download" };
    private readonly AriaRuntimeClientRegistry _ariaClientRegistry;
    private readonly ILogger<DownloadTaskFileService> _logger;
    private readonly IDownloadOutputArtifactProvenanceApplicationService? _outputProvenance;
    private readonly IOutputArtifactOwnershipProvider? _artifactOwnershipProvider;

    public DownloadTaskFileService(
        AriaRuntimeClientRegistry ariaClientRegistry,
        ILogger<DownloadTaskFileService> logger,
        IDownloadOutputArtifactProvenanceApplicationService? outputProvenance = null,
        IOutputArtifactOwnershipProvider? artifactOwnershipProvider = null)
    {
        _ariaClientRegistry = ariaClientRegistry
            ?? throw new ArgumentNullException(nameof(ariaClientRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _outputProvenance = outputProvenance;
        _artifactOwnershipProvider = artifactOwnershipProvider;
    }

    public async Task CancelActiveDownloadAsync(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);

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

        var ariaClient = _ariaClientRegistry.Current;
        if (ariaClient == null)
        {
            _logger.LogDebugMessage("Cancel aria downloader skipped because no aria2 runtime is active.");
            return;
        }

        try
        {
            await ariaClient.RemoveAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await ariaClient.RemoveDownloadResultAsync(gid).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException e)
        {
            _logger.LogDebugMessage($"Cancel aria downloader failed: {e.Message}");
        }
        catch (HttpRequestException e)
        {
            _logger.LogDebugMessage($"Cancel aria downloader failed: {e.Message}");
        }
    }

    public async Task<DownloadOutputArtifactCleanupResult> DeleteGeneratedFilesAsync(
        DownloadingItem downloading,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        cancellationToken.ThrowIfCancellationRequested();
        var discovered = GetGeneratedFiles(downloading);
        var taskId = new DownloadTaskId(downloading.DownloadBase.Id);
        if (_outputProvenance == null || _artifactOwnershipProvider == null)
        {
            return CreateUnprovenDiscoveryResult(discovered);
        }

        var loaded = await _outputProvenance
            .GetPublishedAsync(taskId, cancellationToken)
            .ConfigureAwait(false);
        if (!loaded.TryGetValue(out var provenance))
        {
            _logger.LogWarningMessage(
                "Final output provenance could not be loaded; automatic cleanup will preserve discovered files.");
            return CreateFailedDiscoveryResult(discovered);
        }

        var entries = new List<DownloadOutputArtifactCleanupEntry>();
        var provenPaths = OperatingSystem.IsWindows()
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in provenance)
        {
            cancellationToken.ThrowIfCancellationRequested();
            provenPaths.Add(artifact.CanonicalPath);
            OutputArtifactSafeDeleteResult deletion;
            try
            {
                deletion = await _artifactOwnershipProvider
                    .DeleteIfOwnedAsync(
                        artifact.CanonicalPath,
                        artifact,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                _logger.LogErrorMessage("Final output ownership deletion failed.", exception);
                deletion = OutputArtifactSafeDeleteResult.Failed();
            }
            catch (UnauthorizedAccessException exception)
            {
                _logger.LogErrorMessage("Final output ownership deletion was denied.", exception);
                deletion = OutputArtifactSafeDeleteResult.Failed();
            }

            var status = MapSafeDeleteStatus(deletion.Status);
            entries.Add(new DownloadOutputArtifactCleanupEntry(
                artifact.ArtifactKey,
                artifact.CanonicalPath,
                status));
            if (status is not DownloadOutputArtifactCleanupStatus.Deleted and
                not DownloadOutputArtifactCleanupStatus.Missing)
            {
                _logger.LogWarningMessage(
                    $"Final output cleanup preserved an artifact. key={artifact.ArtifactKey}; outcome={status}.");
            }
        }

        foreach (var candidate in discovered)
        {
            if (provenPaths.Contains(candidate) || !File.Exists(candidate))
            {
                continue;
            }

            entries.Add(new DownloadOutputArtifactCleanupEntry(
                ArtifactKey: null,
                candidate,
                DownloadOutputArtifactCleanupStatus.PreservedUnproven));
            _logger.LogWarningMessage(
                "Final-output cleanup preserved a discovered file without provenance.");
        }

        return new DownloadOutputArtifactCleanupResult(entries);
    }

    internal Task<DownloadFileDeletionResult> DeleteFilesAsync(
        IEnumerable<string> files,
        CancellationToken cancellationToken = default)
    {
        // File.Delete has no async API and can block on network drives or antivirus scans.
        return Task.Run(() => DeleteFilesCoreAsync(files, cancellationToken), cancellationToken);
    }

    internal Task<DownloadFileDeletionResult> DeleteTransferFilesAsync(
        IEnumerable<string> transferFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transferFiles);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transferFile in transferFiles)
        {
            if (!string.IsNullOrWhiteSpace(transferFile))
            {
                AddWithTempFiles(files, Path.GetFullPath(transferFile));
            }
        }

        return DeleteFilesAsync(files, cancellationToken);
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

    private static DownloadOutputArtifactCleanupResult CreateUnprovenDiscoveryResult(
        IEnumerable<string> discovered)
    {
        return new DownloadOutputArtifactCleanupResult(
            discovered
                .Where(File.Exists)
                .Select(path => new DownloadOutputArtifactCleanupEntry(
                    ArtifactKey: null,
                    path,
                    DownloadOutputArtifactCleanupStatus.PreservedUnproven))
                .ToArray());
    }

    private static DownloadOutputArtifactCleanupResult CreateFailedDiscoveryResult(
        IEnumerable<string> discovered)
    {
        var entries = discovered
            .Where(File.Exists)
            .Select(path => new DownloadOutputArtifactCleanupEntry(
                ArtifactKey: null,
                path,
                DownloadOutputArtifactCleanupStatus.PreservedUnproven))
            .ToList();
        entries.Add(new DownloadOutputArtifactCleanupEntry(
            ArtifactKey: null,
            Path: string.Empty,
            DownloadOutputArtifactCleanupStatus.Failed));
        return new DownloadOutputArtifactCleanupResult(entries);
    }

    private static DownloadOutputArtifactCleanupStatus MapSafeDeleteStatus(
        OutputArtifactSafeDeleteStatus status)
    {
        return status switch
        {
            OutputArtifactSafeDeleteStatus.Deleted => DownloadOutputArtifactCleanupStatus.Deleted,
            OutputArtifactSafeDeleteStatus.Missing => DownloadOutputArtifactCleanupStatus.Missing,
            OutputArtifactSafeDeleteStatus.Replaced => DownloadOutputArtifactCleanupStatus.PreservedReplaced,
            OutputArtifactSafeDeleteStatus.Modified => DownloadOutputArtifactCleanupStatus.PreservedModified,
            OutputArtifactSafeDeleteStatus.Unsupported => DownloadOutputArtifactCleanupStatus.PreservedUnsupported,
            OutputArtifactSafeDeleteStatus.Unproven => DownloadOutputArtifactCleanupStatus.PreservedUnproven,
            OutputArtifactSafeDeleteStatus.Failed => DownloadOutputArtifactCleanupStatus.Failed,
            _ => DownloadOutputArtifactCleanupStatus.Failed
        };
    }
}

internal readonly record struct DownloadFileDeletionResult(int AttemptedCount, int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
