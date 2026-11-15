using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed record DownloadTransferRequest(
    DownloadTaskId TaskId,
    IReadOnlyList<string> Urls,
    string Directory,
    string FileName,
    long ExpectedBytes,
    Action EnsureActive,
    Func<bool> IsPauseRequested,
    Action<DownloadProgress> PublishProgress,
    Func<DownloadProgress, CancellationToken, Task> PersistProgressAsync,
    CancellationToken CancellationToken,
    string? StagingDirectory = null,
    Action<bool>? NetworkReadStateChanged = null);

internal interface ITransferBackend
{
    string Name { get; }

    Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request);
}

internal enum DownloadTransferOutcome
{
    Failed,
    Succeeded,
    Paused
}

internal enum DownloadTransferFailureKind
{
    None,
    TransientNetwork,
    RateLimited,
    ExpiredAddress,
    ResumeRejected,
    InvalidMedia,
    CandidateRejected,
    Disk,
    Tls,
    Permanent,
    SlowSource
}

internal sealed record DownloadTransferResult(
    DownloadTransferOutcome Outcome,
    DownloadTransferFailureKind FailureKind,
    string ErrorCode,
    TimeSpan? RetryAfter = null)
{
    public static DownloadTransferResult Succeeded() =>
        new(
            DownloadTransferOutcome.Succeeded,
            DownloadTransferFailureKind.None,
            string.Empty);

    public static DownloadTransferResult Paused() =>
        new(
            DownloadTransferOutcome.Paused,
            DownloadTransferFailureKind.None,
            string.Empty);

    public static DownloadTransferResult Failed(
        DownloadTransferFailureKind failureKind,
        string errorCode,
        TimeSpan? retryAfter = null) =>
        new(
            DownloadTransferOutcome.Failed,
            failureKind,
            errorCode,
            retryAfter);
}
