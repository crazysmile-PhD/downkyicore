using System;
using System.IO;
using System.Net.Http;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        cancellationToken.ThrowIfCancellationRequested();
        if (_staging != null && downloading.DownloadBase is { } downloadBase)
        {
            _staging.CleanupTask(new(downloadBase.Id));
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
            context.TaskId, key, destination, CancellationToken.None).ConfigureAwait(true);
        context.PublishedKeys.Add(key);
        return OperationResult.Success();
    }

    public void CleanupStaging(DownloadTaskId taskId) => _staging?.CleanupTask(taskId);

}

internal readonly record struct DownloadFileDeletionResult(int AttemptedCount, int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
