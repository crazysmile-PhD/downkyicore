using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Client.Entity;
using DownKyi.Core.Logging;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.Aria2cNet;

public class AriaManager
{
    private const int PollDelayMilliseconds = 500;

    // gid对应项目的状态
    public delegate void TellStatusHandler(long totalLength, long completedLength, long speed, string gid);

    public event TellStatusHandler? TellStatus;

    protected virtual void OnTellStatus(long totalLength, long completedLength, long speed, string gid)
    {
        TellStatus?.Invoke(totalLength, completedLength, speed, gid);
    }

    // 下载结果回调
    public delegate void DownloadFinishHandler(bool isSuccess, string? downloadPath, string gid, string? msg = null);

    public event DownloadFinishHandler? DownloadFinish;

    protected virtual void OnDownloadFinish(bool isSuccess, string? downloadPath, string gid, string? msg = null)
    {
        DownloadFinish?.Invoke(isSuccess, downloadPath, gid, msg);
    }

    // 全局下载状态
    public delegate void GetGlobalStatusHandler(long speed);

    public event GetGlobalStatusHandler? GlobalStatus;

    protected virtual void OnGlobalStatus(long speed)
    {
        GlobalStatus?.Invoke(speed);
    }

    /// <summary>
    /// 获取gid下载项的状态。
    /// </summary>
    public DownloadResult GetDownloadStatus(string gid, Action? action = null)
    {
        return GetDownloadStatusAsync(gid, action).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 获取gid下载项的状态。
    /// </summary>
    public async Task<DownloadResult> GetDownloadStatusAsync(
        string gid,
        Action? action = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gid))
        {
            return DownloadResult.FAILED;
        }

        string? filePath = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await AriaClient.TellStatus(gid).ConfigureAwait(false);
            if (status?.Result == null)
            {
                if (status?.Error?.Message?.Contains("is not found", StringComparison.OrdinalIgnoreCase) == true)
                {
                    OnDownloadFinish(false, null, gid, status.Error.Message);
                    return DownloadResult.ABORT;
                }

                await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                continue;
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
            action?.Invoke();

            if (result.Status == "complete")
            {
                OnDownloadFinish(true, filePath, gid, null);
                return DownloadResult.SUCCESS;
            }

            if (!string.IsNullOrEmpty(result.ErrorCode) && result.ErrorCode != "0")
            {
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    Console.PrintLine("ErrorMessage: " + result.ErrorMessage);
                    LogManager.Error("AriaManager", result.ErrorMessage);
                }

                var ariaRemove = await AriaClient.RemoveDownloadResultAsync(gid).ConfigureAwait(false);
                if (ariaRemove?.Result != null)
                {
                    LogManager.Debug("AriaManager", ariaRemove.Result);
                }

                OnDownloadFinish(false, null, gid, result.ErrorMessage);
                return DownloadResult.FAILED;
            }

            await Task.Delay(PollDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 获取全局下载速度。
    /// </summary>
    public void GetGlobalStatus()
    {
        _ = GetGlobalStatusAsync();
    }

    /// <summary>
    /// 获取全局下载速度。
    /// </summary>
    public async Task GetGlobalStatusAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var globalStatus = await AriaClient.GetGlobalStatAsync().ConfigureAwait(false);
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
