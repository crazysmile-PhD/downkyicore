using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

internal static class DownloadExecutionContextTestFactory
{
    public static DownloadExecutionContext Create(
        DownloadingItem downloading,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        ArgumentNullException.ThrowIfNull(settings);
        var queuedProjection = new DownloadingItem
        {
            DownloadBase = downloading.DownloadBase,
            Metadata = downloading.Metadata,
            Downloading = new Downloading
            {
                DownloadStatus = DownloadStatus.WaitForDownload,
                DownloadFiles = downloading.Downloading.DownloadFiles,
                PlayStreamType = downloading.Downloading.PlayStreamType
            }
        };
        var task = DownloadTaskProjectionMapper.CreateNewTask(
            queuedProjection,
            DateTimeOffset.UnixEpoch);
        return new DownloadExecutionContext(
            task.Id,
            DownloadExecutionContextFactory.CreateInput(task, settings),
            downloading.PlayUrl,
            static (_, token) => token.ThrowIfCancellationRequested());
    }
}
