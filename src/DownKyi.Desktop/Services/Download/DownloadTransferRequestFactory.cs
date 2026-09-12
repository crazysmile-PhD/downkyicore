using System;
using System.Collections.Generic;
using System.Threading;
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
        DownloadTaskProjectionStore projections,
        DownloadTaskStateWriter stateWriter,
        Action ensureActive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(stateWriter);
        ArgumentNullException.ThrowIfNull(ensureActive);
        var projection = projections.GetRequiredDownloadingProjection(taskId);
        var snapshot = projections.GetRequiredSnapshot(taskId);
        return new DownloadTransferRequest(
            taskId,
            snapshot.Transfer.BackendIdentity,
            urls,
            path,
            localFileName,
            expectedBytes,
            ensureActive,
            () => projections.GetRequiredSnapshot(taskId).Phase is
                DownloadPhase.Pausing or DownloadPhase.Paused,
            progress => projections.PublishLiveProgress(taskId, progress),
            (progress, token) => stateWriter.UpdateProgressAsync(taskId, progress, token),
            (backendIdentity, token) => stateWriter.SetBackendIdentityAsync(
                taskId,
                backendIdentity,
                token),
            service => projection.DownloadService = service,
            cancellationToken,
            stagingDirectory);
    }
}
