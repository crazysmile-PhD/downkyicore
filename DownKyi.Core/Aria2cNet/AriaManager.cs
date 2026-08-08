using DownKyi.Application.Diagnostics;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.Aria2cNet;

public sealed class AriaProgressEventArgs(long totalLength, long completedLength, long speed, string gid) : EventArgs
{
    public long TotalLength { get; } = totalLength;
    public long CompletedLength { get; } = completedLength;
    public long Speed { get; } = speed;
    public string Gid { get; } = gid;
}

public sealed class AriaDownloadCompletedEventArgs(
    bool isSuccess,
    string? downloadPath,
    string gid,
    string? message) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;
    public string? DownloadPath { get; } = downloadPath;
    public string Gid { get; } = gid;
    public string? Message { get; } = message;
}

public sealed class AriaGlobalStatusEventArgs(long speed) : EventArgs
{
    public long Speed { get; } = speed;
}

public sealed record AriaDownloadStatus(
    DownloadResult Result,
    string? ErrorCode,
    string? ErrorMessage);

public class AriaManager
{
    private const int PollDelayMilliseconds = 500;
    private readonly AriaClient _ariaClient;
    private readonly ILogger<AriaManager> _logger;

    public AriaManager(AriaClient ariaClient, ILogger<AriaManager> logger)
    {
        _ariaClient = ariaClient ?? throw new ArgumentNullException(nameof(ariaClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // gid对应项目的状态
    public event EventHandler<AriaProgressEventArgs>? TellStatus;

    protected virtual void OnTellStatus(long totalLength, long completedLength, long speed, string gid)
    {
        TellStatus?.Invoke(this, new AriaProgressEventArgs(totalLength, completedLength, speed, gid));
    }

    // 下载结果回调
    public event EventHandler<AriaDownloadCompletedEventArgs>? DownloadFinish;

    protected virtual void OnDownloadFinish(bool isSuccess, string? downloadPath, string gid, string? msg = null)
    {
        DownloadFinish?.Invoke(this, new AriaDownloadCompletedEventArgs(isSuccess, downloadPath, gid, msg));
    }

    // 全局下载状态
    public event EventHandler<AriaGlobalStatusEventArgs>? GlobalStatus;

    protected virtual void OnGlobalStatus(long speed)
    {
        GlobalStatus?.Invoke(this, new AriaGlobalStatusEventArgs(speed));
    }

    /// <summary>
    /// 获取gid下载项的状态。
    /// </summary>
    public async Task<DownloadResult> GetDownloadStatusAsync(
        string gid,
        Func<CancellationToken, ValueTask>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        var status = await GetDownloadStatusDetailAsync(
            gid,
            statusCallback,
            cancellationToken).ConfigureAwait(false);
        return status.Result;
    }

    /// <summary>
    /// Gets the download status while preserving aria2's machine-readable failure code.
    /// </summary>
    public async Task<AriaDownloadStatus> GetDownloadStatusDetailAsync(
        string gid,
        Func<CancellationToken, ValueTask>? statusCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid))
        {
            return new AriaDownloadStatus(
                DownloadResult.FAILED,
                "invalid-gid",
                null);
        }

        string? filePath = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await _ariaClient.TellStatus(gid).ConfigureAwait(false);
            if (status?.Result == null)
            {
                if (status?.Error is { } rpcError)
                {
                    var errorCode = rpcError.Message.Contains(
                        "is not found",
                        StringComparison.OrdinalIgnoreCase)
                        ? "not-found"
                        : $"rpc-{rpcError.Code}";
                    OnDownloadFinish(false, null, gid, rpcError.Message);
                    return new AriaDownloadStatus(
                        errorCode == "not-found"
                            ? DownloadResult.ABORT
                            : DownloadResult.FAILED,
                        errorCode,
                        rpcError.Message);
                }

                OnDownloadFinish(false, null, gid, null);
                return new AriaDownloadStatus(
                    DownloadResult.FAILED,
                    "rpc-empty",
                    null);
            }

            var result = status.Result;
            if (result.Files?.Count >= 1)
            {
                filePath = result.Files[0].Path;
            }

            var totalLength = ParseLong(result.TotalLength);
            var completedLength = ParseLong(result.CompletedLength);
            var speed = ParseLong(result.DownloadSpeed);

            // 回调
            OnTellStatus(totalLength, completedLength, speed, gid);

            // 在外部执行
            if (statusCallback != null)
            {
                await statusCallback(cancellationToken).ConfigureAwait(false);
            }

            if (result.Status == "complete")
            {
                OnDownloadFinish(true, filePath, gid, null);
                return new AriaDownloadStatus(
                    DownloadResult.SUCCESS,
                    null,
                    null);
            }

            if (!string.IsNullOrEmpty(result.ErrorCode) && result.ErrorCode != "0")
            {
                _logger.LogErrorMessage(
                    $"aria2 reported a download failure; errorCode={result.ErrorCode}.");

                var ariaRemove = await _ariaClient
                    .RemoveDownloadResultAsync(gid, cancellationToken)
                    .ConfigureAwait(false);
                if (ariaRemove?.Result != null)
                {
                    _logger.LogDebugMessage("aria2 removed the failed download result.");
                }

                OnDownloadFinish(false, null, gid, result.ErrorMessage);
                return new AriaDownloadStatus(
                    DownloadResult.FAILED,
                    result.ErrorCode,
                    result.ErrorMessage);
            }

            await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 获取全局下载速度。
    /// </summary>
    public async Task GetGlobalStatusAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var globalStatus = await _ariaClient.GetGlobalStatAsync().ConfigureAwait(false);
            if (globalStatus?.Result == null)
            {
                await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                continue;
            }

            OnGlobalStatus(ParseLong(globalStatus.Result.DownloadSpeed));

            await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private static long ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : 0;
    }
}
