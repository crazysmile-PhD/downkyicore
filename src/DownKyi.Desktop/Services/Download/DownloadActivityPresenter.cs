using System;
using System.IO;
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
        if (error == null)
        {
            return CreateRetryableFailure();
        }

        if (error.Kind == OperationErrorKind.NotFound)
        {
            return new DownloadFailure(
                error.Code,
                error.Message,
                false);
        }

        if (IsSubtitleFailureCode(error.Code))
        {
            return new DownloadFailure(
                error.Code,
                DictionaryResource.GetString("SubtitleDownloadFailed"),
                true);
        }

        if (error.Code is "download.media.recreate-task"
            or "download.resolve.recreate-task"
            or "download.media.contract"
            or "download.media.durl-audio-only"
            or "download.cover.convert"
            or "download.validate.published-missing")
        {
            return new DownloadFailure(
                error.Code,
                DictionaryResource.GetString("DownloadFailed"),
                false);
        }

        var resourceKey = error.Code switch
        {
            "download.transfer.tls.untrusted" => "DownloadTlsUntrusted",
            "download.transfer.tls.expired" => "DownloadTlsExpired",
            "download.transfer.tls.not-yet-valid" => "DownloadTlsNotYetValid",
            "download.transfer.tls.hostname" => "DownloadTlsHostnameMismatch",
            "download.transfer.tls.chain" => "DownloadTlsInvalidChain",
            "download.transfer.tls.handshake" => "DownloadTlsHandshakeFailed",
            _ => "DownloadFailed"
        };
        return new DownloadFailure(
            error.Code,
            DictionaryResource.GetString(resourceKey),
            true);
    }

    public static string FormatFailureDetails(DownloadFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var resourceKey = GetFailureDetailsResourceKey(failure.Code);
        var code = IsSafeDiagnosticCode(failure.Code)
            ? failure.Code
            : DictionaryResource.GetString("DownloadFailureUnknownCode");
        return $"{DictionaryResource.GetString(resourceKey)}\n\n" +
               $"{DictionaryResource.GetString("DownloadFailureCode")}: {code}";
    }

    private static string GetFailureDetailsResourceKey(string code)
    {
        return code switch
        {
            DownloadTaskStateWriter.LegacyBackendRemovedFailureCode
                or "download.transfer.legacy-cleanup-failed" => "DownloadFailureLegacyDetails",

            "download.runtime.unavailable" => "DownloadFailureRuntimeDetails",

            "download.transfer.tls.untrusted"
                or "download.transfer.tls.expired"
                or "download.transfer.tls.not-yet-valid"
                or "download.transfer.tls.hostname"
                or "download.transfer.tls.chain"
                or "download.transfer.tls.handshake" => "DownloadFailureTlsDetails",

            "download.transfer.http-429"
                or "download.artifact.cover.http-429"
                or "download.artifact.danmaku.http-429"
                or "download.artifact.subtitle.http-429" => "DownloadFailureRateLimitDetails",

            "download.transfer.http-401"
                or "download.transfer.http-client-error" => "DownloadFailureAccessDetails",

            "download.artifact.cover.http-401"
                or "download.artifact.cover.http-403" => "DownloadFailureCoverAccessDetails",

            "download.artifact.danmaku.http-401"
                or "download.artifact.subtitle.http-401"
                or "download.artifact.danmaku.http-403"
                or "download.artifact.subtitle.http-403" => "DownloadFailureArtifactAccessDetails",

            "download.transfer.http-403"
                or "download.transfer.http-404" => "DownloadFailureExpiredAddressDetails",

            "download.playback.selection-unavailable"
            or "download.media.recreate-task"
            or "download.resolve.recreate-task"
            or "download.media.contract"
            or "download.media.durl-audio-only"
            or "download.media.missing"
            or "download.media.descriptor"
            or "download.media.url"
            or "download.media.audio"
            or "download.media.video"
            or "download.media.durl"
            or "download.validate.no-resource"
            or "download.transfer.no-address"
            or "download.finalize.no-output" => "DownloadFailureUnavailableDetails",

            "download.transfer.disk"
            or "download.media.directory"
            or "download.resolve.directory"
            or "download.publish.collision"
            or DownloadArtifactWriter.SubtitleOutputWriteFailureCode
            or "download.transfer.cleanup"
            or "download.transfer.cleanup-failed"
            or "download.transfer.source-change-cleanup"
            or "download.artifact.cover.permission"
            or "download.artifact.danmaku.permission"
            or "download.artifact.subtitle.permission"
            or "download.artifact.nfo.io"
            or "download.artifact.nfo.permission" => "DownloadFailureStorageDetails",

            "download.transfer.invalid-media"
            or "download.media.durl-manifest"
            or "download.validate.media"
            or "download.validate.unpublished-invalid"
            or "download.artifact.cover.invalid"
            or "download.artifact.danmaku.invalid"
            or "download.artifact.subtitle.invalid"
            or "download.artifact.nfo.invalid" => "DownloadFailureInvalidOutputDetails",

            "download.validate.subtitle"
            or "download.artifact.subtitle.parse"
            or "download.artifact.subtitle.duplicate-track"
            or "download.artifact.subtitle.missing-tracks"
            or "download.artifact.subtitle.duplicate-output" => "DownloadFailureSubtitleDetails",

            "download.validate.danmaku"
            or "download.artifact.danmaku.parse"
            or "download.artifact.danmaku.xml"
            or "download.artifact.danmaku.data"
            or "download.artifact.danmaku.io"
            or "download.artifact.danmaku.snapshot-incomplete"
            or "download.artifact.cover.io"
            or "download.artifact.nfo.xml"
            or "download.artifact.unknown" => "DownloadFailureArtifactDetails",

            "download.mux.media-kind"
            or "download.mux.dash"
            or "download.mux.durl-input"
            or "download.mux.durl"
            or "download.mux.concat"
            or "download.cover.process"
            or "download.finalize.unvalidated"
            or "download.publish.unvalidated"
            or "download.publish.invalid" => "DownloadFailureProcessingDetails",

            "download.cover.convert" => "DownloadFailureCoverConversionDetails",

            "download.publish.reconcile" => "DownloadFailurePublicationRecoveryDetails",

            "download.validate.published-missing" => "DownloadFailurePublishedArtifactDetails",

            "download.transfer.network"
            or "download.transfer.timeout"
            or "download.transfer.http-408"
            or "download.transfer.http-server-error"
            or "download.transfer.resume-rejected"
            or "download.transfer.redirect-limit"
            or "download.transfer.redirect-location-missing"
            or DownloadArtifactWriter.SubtitleResponseReadFailureCode
            or "download.artifact.cover.http"
            or "download.artifact.cover.http-408"
            or "download.artifact.cover.http-server-error"
            or "download.artifact.danmaku.http"
            or "download.artifact.danmaku.http-408"
            or "download.artifact.danmaku.http-server-error"
            or "download.artifact.subtitle.http"
            or "download.artifact.subtitle.http-408"
            or "download.artifact.subtitle.http-server-error" => "DownloadFailureNetworkDetails",

            _ => "DownloadFailureGenericDetails"
        };
    }

    private static bool IsSubtitleFailureCode(string code)
    {
        return code is DownloadArtifactWriter.SubtitleResponseReadFailureCode
            or DownloadArtifactWriter.SubtitleOutputWriteFailureCode
            or "download.artifact.subtitle.http"
            or "download.artifact.subtitle.http-401"
            or "download.artifact.subtitle.http-403"
            or "download.artifact.subtitle.http-408"
            or "download.artifact.subtitle.http-429"
            or "download.artifact.subtitle.http-server-error"
            or "download.artifact.subtitle.parse"
            or "download.artifact.subtitle.duplicate-track"
            or "download.artifact.subtitle.missing-tracks"
            or "download.artifact.subtitle.duplicate-output"
            or "download.artifact.subtitle.invalid"
            or "download.artifact.subtitle.permission";
    }

    private static bool IsSafeDiagnosticCode(string code)
    {
        if (code.Length is < 1 or > 100
            || !code.StartsWith("download.", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in code)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static string CreateDirectoryError(string path)
    {
        return $"{path}{DictionaryResource.GetString("DirectoryError")}";
    }

    public static string CreateAdmissionError(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return DictionaryResource.GetString(exception is DownloadOutputCollisionException
            ? "DownloadOutputCollision"
            : "DirectoryError");
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
