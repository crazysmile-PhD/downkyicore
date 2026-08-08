using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;
using Downloader;

namespace DownKyi.Services.Download;

internal sealed record DownloadTransferRequest(
    DownloadTaskId TaskId,
    string? BackendIdentity,
    IReadOnlyList<string> Urls,
    string Directory,
    string FileName,
    long ExpectedBytes,
    Action EnsureActive,
    Func<bool> IsPauseRequested,
    Action<DownloadProgress> PublishProgress,
    Func<DownloadProgress, CancellationToken, Task> PersistProgressAsync,
    Func<string?, CancellationToken, Task> SetBackendIdentityAsync,
    Action<DownloadService?> SetBuiltinDownloadService,
    CancellationToken CancellationToken);

internal interface ITransferBackend : IDisposable
{
    string Name { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task<DownloadTransferResult> ResetAsync(
        string? backendIdentity,
        CancellationToken cancellationToken);

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
    Disk,
    Tls,
    Permanent
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
