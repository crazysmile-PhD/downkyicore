using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal sealed class DownloadTaskStateWriter
{
    private readonly IDownloadTaskApplicationService _tasks;

    public DownloadTaskStateWriter(IDownloadTaskApplicationService tasks)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    public Task<DownloadTask> StartAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.StartAsync(taskId, cancellationToken));

    public Task<DownloadTask> PauseAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.PauseAsync(taskId, cancellationToken));

    public Task<DownloadTask> ConfirmPausedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.ConfirmPausedAsync(taskId, cancellationToken));

    public async Task<DownloadTask> ResumeAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        var current = await FindRequiredAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (current.Phase == DownloadPhase.Queued)
        {
            return current;
        }

        return current.Phase == DownloadPhase.Failed
            ? await RequireAsync(_tasks.RetryAsync(taskId, cancellationToken)).ConfigureAwait(false)
            : await RequireAsync(_tasks.ResumeAsync(taskId, cancellationToken)).ConfigureAwait(false);
    }

    public Task<DownloadTask> RecoverInterruptedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.RecoverInterruptedAsync(taskId, cancellationToken));

    public Task<DownloadTask> FailAsync(
        DownloadTaskId taskId,
        DownloadFailure failure,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.FailAsync(taskId, failure, cancellationToken));

    public Task<DownloadTask> CompleteAsync(
        DownloadTaskId taskId,
        DownloadCompletion completion,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.CompleteAsync(taskId, completion, cancellationToken));

    public Task<DownloadTask> RecordTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.RecordTransferFileAsync(taskId, key, filePath, cancellationToken));

    public Task<DownloadTask> ClaimTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        string filePath,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.ClaimTransferFileAsync(taskId, key, filePath, cancellationToken));

    public Task<DownloadTask> InvalidateCompletedFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.InvalidateCompletedFileAsync(taskId, key, cancellationToken));

    public Task<DownloadTask> CompleteTransferFileAsync(
        DownloadTaskId taskId,
        string key,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.CompleteTransferFileAsync(taskId, key, cancellationToken));

    public Task<DownloadTask> SetBackendIdentityAsync(
        DownloadTaskId taskId,
        string? backendIdentity,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.SetBackendIdentityAsync(taskId, backendIdentity, cancellationToken));

    public Task<DownloadTask> UpdateActivityAsync(
        DownloadTaskId taskId,
        string? activeContent,
        string? statusText,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.UpdateActivityAsync(
            taskId,
            activeContent,
            statusText,
            cancellationToken));

    public Task<DownloadTask> UpdateProgressAsync(
        DownloadTaskId taskId,
        DownloadProgress progress,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.UpdateProgressAsync(taskId, progress, cancellationToken));

    public Task<DownloadTask> UpdateOutputFileSizeAsync(
        DownloadTaskId taskId,
        string? fileSizeText,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.UpdateOutputFileSizeAsync(taskId, fileSizeText, cancellationToken));

    public Task<DownloadTask> CancelAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.CancelAsync(taskId, cancellationToken));

    public Task<DownloadTask> DeleteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default) =>
        RequireAsync(_tasks.DeleteAsync(taskId, cancellationToken));

    private async Task<DownloadTask> FindRequiredAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        return await _tasks.FindAsync(taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Download task '{taskId.Value}' was not found.");
    }

    private static async Task<DownloadTask> RequireAsync(
        Task<OperationResult<DownloadTask>> operation)
    {
        var result = await operation.ConfigureAwait(false);
        if (result.TryGetValue(out var task))
        {
            return task;
        }

        throw new InvalidOperationException(
            result.Error?.Message ?? "Download state update failed.");
    }
}
