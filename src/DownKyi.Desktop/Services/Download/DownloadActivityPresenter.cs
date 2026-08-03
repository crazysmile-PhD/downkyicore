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
    private readonly DownloadTaskStateWriter _stateWriter;

    public DownloadActivityPresenter(DownloadTaskStateWriter stateWriter)
    {
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
    }

    public static void Reset(DownloadingItem downloading)
    {
        ArgumentNullException.ThrowIfNull(downloading);
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

    public static void ShowDownloadingAudio(DownloadingItem downloading)
    {
        ShowTransferActivity(downloading, "DownloadingAudio");
    }

    public static void ShowDownloadingVideo(DownloadingItem downloading)
    {
        ShowTransferActivity(downloading, "DownloadingVideo");
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

    private static void ShowTransferActivity(
        DownloadingItem downloading,
        string contentResourceKey)
    {
        ArgumentNullException.ThrowIfNull(downloading);
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
        var downloading = context.Downloading;
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
}
