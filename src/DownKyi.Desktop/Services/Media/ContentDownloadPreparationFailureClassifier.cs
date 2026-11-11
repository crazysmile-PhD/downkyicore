using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using DownKyi.Core.BiliApi;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Services.Download;
using Newtonsoft.Json;

namespace DownKyi.Services.Media;

internal readonly record struct ContentDownloadPreparationFailure(
    DownloadUnavailableReason Reason,
    int? ApiCode = null);

internal static class ContentDownloadPreparationFailureClassifier
{
    private const int AccountRequiredCode = -101;
    private const int NotFoundCode = -404;
    private const int VideoUnavailableCode = 62002;

    public static bool TryClassify(
        Exception exception,
        CancellationToken callerCancellationToken,
        out ContentDownloadPreparationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ContentDownloadPreparationFailure? classifiedFailure = exception switch
        {
            BilibiliApiResponseException { Code: AccountRequiredCode } apiException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.AccountRequired,
                    apiException.Code),
            BilibiliApiResponseException { Code: NotFoundCode } apiException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.AccessRestrictedOrUnavailable,
                    apiException.Code),
            BilibiliApiResponseException { Code: VideoUnavailableCode } apiException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.VideoUnavailable,
                    apiException.Code),
            BilibiliApiResponseException { Code: null } =>
                new ContentDownloadPreparationFailure(DownloadUnavailableReason.Unknown),
            BilibiliApiResponseException apiException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.ApiRejected,
                    apiException.Code),
            PlaybackUnavailableException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.NoPlaybackResource),
            HttpRequestException or IOException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.NetworkFailure),
            OperationCanceledException when !callerCancellationToken.IsCancellationRequested =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.RequestTimedOut),
            JsonException or System.Text.Json.JsonException or FormatException =>
                new ContentDownloadPreparationFailure(
                    DownloadUnavailableReason.InvalidResponse),
            _ => null
        };

        failure = classifiedFailure.GetValueOrDefault();
        return classifiedFailure.HasValue;
    }
}
