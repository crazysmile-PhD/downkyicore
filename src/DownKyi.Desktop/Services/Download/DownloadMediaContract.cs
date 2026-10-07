using System;
using System.Linq;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal static class DownloadMediaContract
{
    public static DownloadMediaKind Detect(PlayUrl? playUrl)
    {
        if (playUrl == null)
        {
            return DownloadMediaKind.None;
        }

        var availability = PlayUrlAvailability.From(playUrl);
        var hasDash = availability.Video.Any(video =>
                          video.StreamKind == PlayUrlStreamKind.Dash)
                      || availability.Audio.Count > 0;
        var hasDurl = availability.Video.Any(video =>
            video.StreamKind == PlayUrlStreamKind.Durl);
        if (hasDash == hasDurl)
        {
            return DownloadMediaKind.None;
        }

        return hasDash ? DownloadMediaKind.Dash : DownloadMediaKind.Durl;
    }

    public static OperationError? Validate(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.NeedsMedia)
        {
            return context.Input.RequestedContent.MediaKind == DownloadMediaKind.None
                ? null
                : InvalidContract("The stored media contract does not match the requested content.");
        }

        var requestedMediaKind = context.Input.RequestedContent.MediaKind;
        if (requestedMediaKind is null)
        {
            return OperationError.Unexpected(
                "download.media.recreate-task",
                "This unfinished download predates the finalized media contract and must be recreated.");
        }

        return requestedMediaKind.Value switch
        {
            DownloadMediaKind.Dash => ValidateDash(context, playUrl),
            DownloadMediaKind.Durl => ValidateDurl(context, playUrl),
            _ => InvalidContract(
                "The finalized media contract does not contain a downloadable media format.")
        };
    }

    public static PlayUrlDashVideo? SelectAudio(
        DownloadExecutionContext context,
        PlayUrl? playUrl) =>
        DownloadAudioSelection.Select(context.Input.Metadata.AudioCodec.Id, playUrl);

    public static PlayUrlDashVideo? SelectVideo(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        var metadata = context.Input.Metadata;
        return playUrl?.Dash?.Video?.FirstOrDefault(item =>
        {
            var codec = PlaybackQualityCatalog.GetCodecIds().FirstOrDefault(candidate =>
                candidate.Id == item.CodecId);
            return item.Id == metadata.Resolution.Id &&
                   codec?.Name == metadata.VideoCodecName;
        });
    }

    private static OperationError? ValidateDash(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        var selectedAudio = SelectAudio(context, playUrl);
        if (context.NeedsPendingAudio
            && !PlayUrlAvailability.HasUsableAddress(selectedAudio))
        {
            return SelectionUnavailable("The finalized audio stream is unavailable.");
        }

        var selectedVideo = SelectVideo(context, playUrl);
        return context.NeedsPendingVideo && !PlayUrlAvailability.HasUsableAddress(selectedVideo)
            ? SelectionUnavailable("The finalized video stream is unavailable.")
            : null;
    }

    private static OperationError? ValidateDurl(
        DownloadExecutionContext context,
        PlayUrl? playUrl)
    {
        if (context.NeedsAudio && !context.NeedsVideo)
        {
            return OperationError.Unexpected(
                "download.media.durl-audio-only",
                "Audio-only DURL downloads are not supported.");
        }

        var metadata = context.Input.Metadata;
        if (playUrl == null
            || playUrl.Quality != metadata.Resolution.Id
            || !PlayUrlAvailability.HasUsableDurl(playUrl))
        {
            return SelectionUnavailable(
                "The refreshed DURL quality does not match the finalized selection.");
        }

        var codec = PlaybackQualityCatalog.GetCodecIds().FirstOrDefault(candidate =>
            candidate.Id == playUrl.VideoCodecid);
        return codec?.Name != metadata.VideoCodecName
            ? SelectionUnavailable(
                "The refreshed DURL codec does not match the finalized selection.")
            : null;
    }

    internal static OperationError SelectionUnavailable(string message) =>
        new(
            "download.playback.selection-unavailable",
            message,
            OperationErrorKind.NotFound);

    private static OperationError InvalidContract(string message) =>
        OperationError.Unexpected("download.media.contract", message);
}
