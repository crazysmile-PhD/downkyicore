using System.Collections.Immutable;
using DownKyi.Application.FileName;
using DownKyi.Domain.Downloads;

namespace DownKyi.Application.Settings;

public static class ApplicationSettingsDefaults
{
    public const int HighSpeedBuiltInSplit = 16;

    public static ImmutableArray<FileNamePart> FileNameParts { get; } =
    [
        FileNamePart.MainTitle,
        FileNamePart.Slash,
        FileNamePart.Section,
        FileNamePart.Slash,
        FileNamePart.Order,
        FileNamePart.Hyphen,
        FileNamePart.PageTitle,
        FileNamePart.Hyphen,
        FileNamePart.VideoQuality,
        FileNamePart.Hyphen,
        FileNamePart.VideoCodec
    ];
}

public sealed record ApplicationSettings(
    int SchemaVersion,
    BasicApplicationSettings Basic,
    NetworkApplicationSettings Network,
    VideoApplicationSettings Video,
    DanmakuApplicationSettings Danmaku,
    AboutApplicationSettings About,
    UserApplicationSettings User,
    WindowApplicationSettings Window);

public sealed record BasicApplicationSettings(
    ThemeMode ThemeMode,
    AfterDownloadOperation AfterDownload,
    AllowStatus IsListenClipboard,
    DownloadFinishedSort DownloadFinishedSort)
{
    public UiLanguage UiLanguage { get; init; }
}

public sealed record NetworkApplicationSettings(
    AllowStatus IsLiftingOfRegion,
    string UserAgent,
    AllowStatus HighSpeedDownloadMode,
    NetworkProxy NetworkProxy,
    string CustomNetworkProxy,
    int MaxCurrentDownloads,
    int Split,
    int MaxOverallDownloadLimit,
    int MaxDownloadLimit,
    AllowStatus IsHttpProxy,
    string HttpProxy,
    int HttpProxyListenPort);

public sealed record VideoApplicationSettings(
    int VideoCodecs,
    int Quality,
    int AudioQuality,
    AllowStatus IsTranscodingFlvToMp4,
    FfmpegHardwareAcceleration FfmpegHardwareAcceleration,
    int FfmpegMaxParallelJobs,
    string SaveVideoRootPath,
    ImmutableArray<string> HistoryVideoRootPaths,
    AllowStatus IsUseSaveVideoRootPath,
    VideoContentApplicationSettings Content,
    ImmutableArray<FileNamePart> FileNameParts,
    string FileNamePartTimeFormat,
    OrderFormat OrderFormat)
{
    public bool EmbedCover { get; init; } = true;
}

public sealed record VideoContentApplicationSettings(
    bool DownloadAudio,
    bool DownloadVideo,
    bool DownloadDanmaku,
    bool DownloadSubtitle,
    bool DownloadCover,
    bool GenerateMovieMetadata,
    SubtitleLanguagePreference PreferredSubtitleLanguage,
    SubtitleTypePreference PreferredSubtitleType);

public sealed record DanmakuApplicationSettings(
    DanmakuOutputFormat OutputFormat,
    AllowStatus TopFilter,
    AllowStatus BottomFilter,
    AllowStatus ScrollFilter,
    AllowStatus RemoveEmojiAndSpecialCharacters,
    ImmutableArray<string> BlockedKeywords,
    ImmutableArray<long> BlockedSenderUids,
    AllowStatus IsCustomResolution,
    int ScreenWidth,
    int ScreenHeight,
    string FontName,
    int FontSize,
    int LineCount,
    DanmakuLayoutAlgorithm LayoutAlgorithm);

public sealed record AboutApplicationSettings(
    AllowStatus IsReceiveBetaVersion,
    AllowStatus AutoUpdateWhenLaunch,
    string SkipVersionOnLaunch);

public sealed record UserApplicationSettings(
    long Mid,
    string Name,
    bool IsLogin,
    bool IsVip,
    string ImgKey,
    string SubKey);

public sealed record WindowApplicationSettings(
    double Width,
    double Height,
    double X,
    double Y);
