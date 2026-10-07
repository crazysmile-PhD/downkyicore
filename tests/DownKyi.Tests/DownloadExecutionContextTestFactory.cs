using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

internal static class DownloadExecutionContextTestFactory
{
    public static DownloadExecutionContext Create(
        DownloadingItem downloading,
        ApplicationSettings settings) => Create(
            downloading,
            settings,
            resolvedPlayUrl: null);

    public static DownloadExecutionContext Create(
        DownloadingItem downloading,
        ApplicationSettings settings,
        PlayUrl? resolvedPlayUrl)
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
        var context = new DownloadExecutionContext(
            task.Id,
            DownloadExecutionContextFactory.CreateInput(task, settings),
            static (_, token) => token.ThrowIfCancellationRequested());
        context.PlayUrl = resolvedPlayUrl;
        return context;
    }
}
