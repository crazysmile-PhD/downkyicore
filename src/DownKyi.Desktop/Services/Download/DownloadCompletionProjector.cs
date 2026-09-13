using System;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Platform;

namespace DownKyi.Services.Download;

internal sealed class DownloadCompletionProjector
{
    private readonly DownloadListState _downloadLists;
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly IUiDispatcher _uiDispatcher;

    public DownloadCompletionProjector(
        DownloadListState downloadLists,
        DownloadTaskProjectionStore projectionStore,
        IUiDispatcher uiDispatcher)
    {
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public Task ProjectAsync(
        DownloadExecutionContext context,
        DownloadTask completedTask)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completedTask);
        var downloadedItem =
            DownloadTaskProjectionStore.CreateDownloadedProjection(completedTask);
        return _uiDispatcher.InvokeAsync(() =>
        {
            _downloadLists.AddDownloaded(downloadedItem);
            _downloadLists.RemoveDownloading(
                _projectionStore.GetRequiredDownloadingProjection(context.TaskId));
            _downloadLists.SortDownloaded(context.Input.FinishedSort);
        });
    }
}
