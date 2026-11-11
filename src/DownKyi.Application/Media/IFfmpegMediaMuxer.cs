using DownKyi.Application.Settings;

namespace DownKyi.Application.Media;

public enum FfmpegEmbeddedAudioMode
{
    Optional,
    Required,
    Excluded
}

public interface IFfmpegMediaMuxer
{
    Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        VideoApplicationSettings videoSettings,
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputVideo,
        bool overwriteDestination,
        Action<string>? action = null,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? cover = null,
        CancellationToken cancellationToken = default);

    Task<FfmpegOperationResult> MergeMediaAsync(
        VideoApplicationSettings videoSettings,
        string? audio,
        string? video,
        string destination,
        bool overwriteDestination,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? cover = null,
        CancellationToken cancellationToken = default);
}
