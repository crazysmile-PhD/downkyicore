using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskFileService
{
    private readonly AriaRuntimeClientRegistry _ariaClientRegistry;
    private readonly ILogger<DownloadTaskFileService> _logger;
    private readonly DownloadTaskStaging? _staging;
    private readonly DownloadTaskStateWriter? _stateWriter;

    public DownloadTaskFileService(
        AriaRuntimeClientRegistry ariaClientRegistry,
        ILogger<DownloadTaskFileService> logger,
        DownloadTaskStaging? staging = null,
        DownloadTaskStateWriter? stateWriter = null)
    {
        _ariaClientRegistry = ariaClientRegistry
            ?? throw new ArgumentNullException(nameof(ariaClientRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _staging = staging;
        _stateWriter = stateWriter;
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

    public Task<DownloadFileDeletionResult> DeleteGeneratedFilesAsync(
        DownloadingItem downloading,
        DownloadTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        if (downloading.DownloadBase?.Id != task.Id.Value)
        {
            throw new InvalidOperationException("Download task identity changed during deletion.");
        }

        if (_staging != null)
        {
            return Task.FromResult(_staging.CleanupTask(task)
                ? new DownloadFileDeletionResult(1, 0)
                : new DownloadFileDeletionResult(1, 1));
        }

        return Task.FromResult(new DownloadFileDeletionResult(0, 0));
    }

    public async Task<OperationResult> PublishAsync(
        DownloadExecutionContext context,
        string key,
        string stagedFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedFile);
        if (_staging == null || _stateWriter == null || context.StagingDirectory == null)
        {
            throw new InvalidOperationException("Task-private staging is required for publication.");
        }

        var fullSource = Path.GetFullPath(stagedFile);
        var stagingPath = Path.GetFullPath(context.StagingDirectory);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(fullSource), stagingPath, comparison))
        {
            throw new InvalidOperationException("Only a task-private staged file may be published.");
        }

        var outputRoot = Path.GetDirectoryName(Path.GetFullPath(context.Input.OutputBasePath))!;
        var destination = Path.Combine(outputRoot, Path.GetFileName(fullSource));
        context.EnsureActive(cancellationToken);
        var publishing = await FingerprintAsync(key, fullSource, cancellationToken).ConfigureAwait(true);
        await _stateWriter.BeginPublishingArtifactAsync(
            context.TaskId, publishing, cancellationToken).ConfigureAwait(true);
        context.EnsureActive(cancellationToken);
        try
        {
            File.Move(fullSource, destination, overwrite: false);
        }
        catch (IOException)
        {
            return OperationResult.Failure(new OperationError(
                "download.publish.collision",
                "The destination already exists or could not be published.",
                OperationErrorKind.Conflict));
        }

        await _stateWriter.RecordPublishedArtifactAsync(
            context.TaskId, publishing, destination, CancellationToken.None).ConfigureAwait(true);
        context.PublishedArtifacts[key] = destination;
        return OperationResult.Success();
    }

    public async Task<(DownloadTask Task, bool CanRun)> ReconcilePublishingAsync(
        DownloadTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var publishing = task.Output.PublishingArtifact;
        if (publishing == null)
        {
            return (task, true);
        }

        if (_staging == null || _stateWriter == null)
        {
            throw new InvalidOperationException("Task-private staging is required for publication recovery.");
        }

        var directory = _staging.GetDirectory(
            task.Id, task.Output.BasePath, task.Output.StagingToken);
        var stagedFile = Path.Combine(directory, publishing.FileName);
        var destination = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(task.Output.BasePath))!,
            publishing.FileName);
        var stagedExists = File.Exists(stagedFile);
        var destinationExists = File.Exists(destination);
        if (stagedExists)
        {
            if (destinationExists ||
                !await MatchesFingerprintAsync(stagedFile, publishing, cancellationToken)
                    .ConfigureAwait(true))
            {
                return (task, false);
            }

            if (task.Phase == DownloadPhase.Canceled)
            {
                var cleared = await _stateWriter.ClearPublishingArtifactAsync(
                    task.Id, cancellationToken).ConfigureAwait(true);
                return (cleared, false);
            }

            return (task, true); // The existing staged product can retry its non-overwrite move.
        }

        if (!destinationExists)
        {
            var cleared = await _stateWriter.ClearPublishingArtifactAsync(
                task.Id, cancellationToken).ConfigureAwait(true);
            return (cleared, true);
        }

        if (!await MatchesFingerprintAsync(destination, publishing, cancellationToken)
                .ConfigureAwait(true))
        {
            return (task, false);
        }

        var published = await _stateWriter.RecordPublishedArtifactAsync(
            task.Id, publishing, destination, cancellationToken).ConfigureAwait(true);
        return (published, true);
    }

    private static async Task<DownloadPublishingArtifact> FingerprintAsync(
        string key,
        string path,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("A redirected file cannot be published.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return new DownloadPublishingArtifact(key, Path.GetFileName(path), length,
            Convert.ToHexString(digest));
    }

    private static async Task<bool> MatchesFingerprintAsync(
        string path,
        DownloadPublishingArtifact expected,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var actual = await FingerprintAsync(expected.Key, path, cancellationToken)
            .ConfigureAwait(false);
        return actual.Length == expected.Length && actual.Sha256 == expected.Sha256;
    }

    public void CleanupStaging(DownloadTaskId taskId) => _staging?.CleanupTask(taskId);

}

internal readonly record struct DownloadFileDeletionResult(int AttemptedCount, int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
