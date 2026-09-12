using System;
using System.Threading;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal sealed class DownloadExecutionContextFactory
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly ISettingsStore _settingsStore;
    private readonly DownloadTaskStaging? _staging;

    public DownloadExecutionContextFactory(
        DownloadTaskProjectionStore projectionStore,
        ISettingsStore settingsStore,
        DownloadTaskStaging? staging = null)
    {
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _staging = staging;
    }

    public DownloadExecutionContext Create(DownloadTaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        var task = _projectionStore.GetRequiredSnapshot(taskId);
        var settings = _settingsStore.Current;
        var projection = _projectionStore.GetRequiredDownloadingProjection(taskId);
        var context = new DownloadExecutionContext(
            taskId,
            CreateInput(task, settings),
            projection.PlayUrl,
            EnsureActive);
        if (_staging != null)
        {
            context.StagingDirectory = _staging.GetDirectory(
                taskId, task.Output.BasePath, task.Output.StagingToken);
        }

        foreach (var artifact in task.Output.PublishedArtifacts)
        {
            context.PublishedArtifacts.Add(artifact.Key, artifact.Value);
        }

        return context;
    }

    internal static DownloadExecutionInput CreateInput(
        DownloadTask task,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(settings);
        return new DownloadExecutionInput(
            task.Metadata,
            task.Plan.RequestedContent,
            task.Plan.TransferFiles,
            task.Output.BasePath,
            (Core.BiliApi.VideoStream.PlayStreamType)task.Plan.StreamType,
            task.Plan.NfoRequest,
            settings.Video,
            settings.Danmaku,
            settings.Basic.DownloadFinishedSort);
    }

    private void EnsureActive(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = _projectionStore.GetRequiredSnapshot(taskId);
        if (task.Phase != DownloadPhase.Downloading)
        {
            throw new OperationCanceledException("Task is paused or deleted.");
        }
    }
}
