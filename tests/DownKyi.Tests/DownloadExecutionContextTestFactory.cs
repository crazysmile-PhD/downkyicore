using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

internal static class DownloadExecutionContextTestFactory
{
    public static DownloadExecutionContext Create(
        DownloadingItem downloading,
        ApplicationSettings settings,
        PlayUrl? resolvedPlayUrl = null,
        DownloadNfoRequest? nfoRequest = null)
    {
        ArgumentNullException.ThrowIfNull(downloading);
        ArgumentNullException.ThrowIfNull(settings);
        var task = DownloadTaskTestFactory.CreateTask(
            downloading,
            DateTimeOffset.UnixEpoch,
            nfoRequest);
        return Create(task, settings, resolvedPlayUrl);
    }

    public static DownloadExecutionContext Create(
        DownloadTaskDraft draft,
        ApplicationSettings settings,
        PlayUrl? resolvedPlayUrl = null) => Create(draft.CreateTask(DateTimeOffset.UnixEpoch), settings, resolvedPlayUrl);

    private static DownloadExecutionContext Create(
        DownloadTask task,
        ApplicationSettings settings,
        PlayUrl? resolvedPlayUrl)
    {
        var context = new DownloadExecutionContext(
            task.Id,
            DownloadExecutionContextFactory.CreateInput(task, settings),
            static (_, token) => token.ThrowIfCancellationRequested());
        context.PlayUrl = resolvedPlayUrl;
        return context;
    }
}
