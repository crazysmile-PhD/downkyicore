using System;
using System.Collections.Generic;
using System.Threading;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadTransferRequestFactory
{
    public static DownloadTransferRequest Create(
        DownloadTaskId taskId,
        IReadOnlyList<string> urls,
        string path,
        string? stagingDirectory,
        string localFileName,
        long expectedBytes,
        IDownloadTaskLookup tasks,
        Action<DownloadProgress> publishProgress,
        DownloadTaskStateWriter stateWriter,
        Action ensureActive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(publishProgress);
        ArgumentNullException.ThrowIfNull(stateWriter);
        ArgumentNullException.ThrowIfNull(ensureActive);
        return new DownloadTransferRequest(
            taskId,
            urls,
            path,
            localFileName,
            expectedBytes,
            ensureActive,
            () => tasks.GetRequiredSnapshot(taskId).Phase is
                DownloadPhase.Pausing or DownloadPhase.Paused,
            publishProgress,
            (progress, token) => stateWriter.UpdateProgressAsync(taskId, progress, token),
            cancellationToken,
            stagingDirectory);
    }
}
