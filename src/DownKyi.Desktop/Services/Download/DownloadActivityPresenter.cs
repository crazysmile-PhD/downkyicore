using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Images;
using DownKyi.Models;
using DownKyi.Utils;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Services.Download;

internal sealed class DownloadActivityPresenter
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;

    public DownloadActivityPresenter(
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter)
    {
        _projectionStore = projectionStore ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    }

    public void Reset(DownloadExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var downloading = GetProjection(context);
        downloading.DownloadStatusTitle = string.Empty;
        downloading.DownloadContent = string.Empty;
    }

    public Task ShowParsingAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        return ShowActivityAsync(
            context,
            contentResourceKey: null,
            titleResourceKey: "Parsing",
            resetProgress: true,
            cancellationToken);
    }

    public void ShowDownloadingAudio(DownloadExecutionContext context)
    {
        ShowTransferActivity(context, "DownloadingAudio");
    }

    public void ShowDownloadingVideo(DownloadExecutionContext context)
    {
        ShowTransferActivity(context, "DownloadingVideo");
    }

    public Task ShowMuxingAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        return ShowActivityAsync(
            context,
            "DownloadingVideo",
            "MixedFlow",
            resetProgress: false,
            cancellationToken);
    }

    public Task ShowConcatenatingAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        return ShowActivityAsync(
            context,
            "DownloadingVideo",
            "ConcatVideos",
            resetProgress: false,
            cancellationToken);
    }

    public Task ShowDownloadingArtifactAsync(
        DownloadExecutionContext context,
        string contentResourceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentResourceKey);
        return ShowActivityAsync(
            context,
            contentResourceKey,
            "WhileDownloading",
            resetProgress: false,
            cancellationToken);
    }

    public static DownloadFailure CreateRetryableFailure()
    {
        return new DownloadFailure(
            "download.runtime.failed",
            DictionaryResource.GetString("DownloadFailed"),
            true);
    }

    public static DownloadFailure CreateFailure(OperationError? error)
    {
        if (error == null || !TlsFailureClassifier.IsTlsErrorCode(error.Code))
        {
            return CreateRetryableFailure();
        }

        return new DownloadFailure(
            error.Code,
            DictionaryResource.GetString(TlsFailureClassifier.GetResourceKey(error.Code)),
            false);
    }

    public static string CreateDirectoryError(string path)
    {
        return $"{path}{DictionaryResource.GetString("DirectoryError")}";
    }

    private void ShowTransferActivity(
        DownloadExecutionContext context,
        string contentResourceKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        var downloading = GetProjection(context);
        downloading.DownloadStatusTitle = DictionaryResource.GetString("WhileDownloading");
        downloading.DownloadContent = DictionaryResource.GetString(contentResourceKey);
        downloading.DownloadingFileSize = string.Empty;
        downloading.Progress = 0;
        downloading.SpeedDisplay = string.Empty;
    }

    private async Task ShowActivityAsync(
        DownloadExecutionContext context,
        string? contentResourceKey,
        string titleResourceKey,
        bool resetProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var downloading = GetProjection(context);
        downloading.DownloadStatusTitle = DictionaryResource.GetString(titleResourceKey);
        downloading.DownloadContent = contentResourceKey == null
            ? string.Empty
            : DictionaryResource.GetString(contentResourceKey);
        downloading.DownloadingFileSize = string.Empty;
        downloading.SpeedDisplay = string.Empty;
        if (resetProgress)
        {
            downloading.Progress = 0;
        }

        await _stateWriter.UpdateActivityAsync(
            context.TaskId,
            downloading.DownloadContent,
            downloading.DownloadStatusTitle,
            cancellationToken).ConfigureAwait(true);
    }

    private DownloadingItem GetProjection(DownloadExecutionContext context)
    {
        return _projectionStore.GetRequiredDownloadingProjection(context.TaskId);
    }
}
