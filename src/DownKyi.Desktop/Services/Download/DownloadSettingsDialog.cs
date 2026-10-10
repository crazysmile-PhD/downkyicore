using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;

namespace DownKyi.Services.Download;

internal static class DownloadSettingsDialog
{
    private const string SubtitleDiscoveryParameter = "subtitleDiscovery", ResultParameter = "result";

    internal enum SubtitleTrackDiscoveryStatus
    {
        NotAttempted,
        Available,
        NoResource,
        Failed
    }

    internal sealed record SubtitleTrack(long TrackId, string Language, string DisplayLanguage, int Type, string Url);
    internal sealed record SubtitleTrackDiscovery(
        SubtitleTrackDiscoveryStatus Status,
        IReadOnlyList<SubtitleTrack> Tracks);

    public static AppDialogRequest CreateRequest(IReadOnlyList<SubtitleTrack> subtitleTracks) =>
        CreateRequest(new SubtitleTrackDiscovery(
            subtitleTracks.Count > 0
                ? SubtitleTrackDiscoveryStatus.Available
                : SubtitleTrackDiscoveryStatus.NoResource,
            subtitleTracks));

    public static AppDialogRequest CreateRequest(SubtitleTrackDiscovery discovery) =>
        new(AppDialog.DownloadSettings, new Dictionary<string, object?>(StringComparer.Ordinal)
        { [SubtitleDiscoveryParameter] = discovery });

    public static SubtitleTrackDiscovery ReadSubtitleDiscovery(AppDialogRequest request) =>
        request.Parameters?.TryGetValue(SubtitleDiscoveryParameter, out var value) == true &&
        value is SubtitleTrackDiscovery discovery
            ? discovery
            : throw new InvalidOperationException(
                "DownloadSettings request is missing its subtitle discovery result.");

    public static IReadOnlyList<SubtitleTrack> ReadSubtitleTracks(AppDialogRequest request) =>
        ReadSubtitleDiscovery(request).Tracks;
    public static IReadOnlyDictionary<string, object?> EncodeResult(
        string directory,
        DownloadContentSelection requestedContent,
        IReadOnlyList<long>? selectedTrackIds,
        long? defaultTrackId)
    {
        if (selectedTrackIds != null)
        {
            var selected = requestedContent.Subtitle ? ImmutableArray.CreateRange(selectedTrackIds) : [];
            defaultTrackId = defaultTrackId is { } candidate && selected.IndexOf(candidate) >= 0
                ? candidate
                : null;
            requestedContent = requestedContent with
            {
                SelectedSubtitleTrackIds = selected,
                DefaultSubtitleTrackId = defaultTrackId
            };
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        { [ResultParameter] = (directory, requestedContent) };
    }
    public static (string Directory, DownloadContentSelection RequestedContent)? DecodeResult(AppDialogResult result) =>
        result.Outcome != AppDialogOutcome.Accepted
            ? null
            : result.Parameters.TryGetValue(ResultParameter, out var value) &&
              value is ValueTuple<string, DownloadContentSelection> selection
            ? selection
            : throw new InvalidOperationException(
                "Accepted DownloadSettings result is missing its typed result.");
}
