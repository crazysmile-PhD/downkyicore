using System.Collections.Immutable;
using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings.Models;

namespace DownKyi.Infrastructure.Settings;

internal static class SettingsSnapshotModelMapper
{
    public static ApplicationSettings ToSnapshot(
        AppSettings settings,
        string missingSaveVideoRootPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(missingSaveVideoRootPath);
        var content = settings.Video.VideoContent ?? new VideoContentSettings();
        return new ApplicationSettings(
            settings.SchemaVersion,
            new BasicApplicationSettings(
                settings.Basic.ThemeMode,
                settings.Basic.AfterDownload,
                settings.Basic.IsListenClipboard,
                settings.Basic.DownloadFinishedSort,
                settings.Basic.RepeatFileAutoAddNumberSuffix)
            {
                UiLanguage = settings.Basic.UiLanguage
            },
            new NetworkApplicationSettings(
                settings.Network.IsLiftingOfRegion,
                settings.Network.UserAgent ?? string.Empty,
                settings.Network.HighSpeedDownloadMode,
                settings.Network.NetworkProxy,
                settings.Network.CustomNetworkProxy ?? string.Empty,
                settings.Network.MaxCurrentDownloads,
                settings.Network.Split,
                settings.Network.MaxOverallDownloadLimit,
                settings.Network.MaxDownloadLimit,
                settings.Network.IsHttpProxy,
                settings.Network.HttpProxy ?? string.Empty,
                settings.Network.HttpProxyListenPort),
            new VideoApplicationSettings(
                settings.Video.VideoCodecs,
                settings.Video.Quality,
                settings.Video.AudioQuality,
                settings.Video.IsTranscodingFlvToMp4,
                settings.Video.IsTranscodingAacToMp3,
                settings.Video.FfmpegHardwareAcceleration,
                settings.Video.FfmpegMaxParallelJobs,
                settings.Video.SaveVideoRootPath ?? missingSaveVideoRootPath,
                settings.Video.HistoryVideoRootPaths?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
                settings.Video.IsUseSaveVideoRootPath,
                new VideoContentApplicationSettings(
                    content.DownloadAudio,
                    content.DownloadVideo,
                    content.DownloadDanmaku,
                    content.DownloadSubtitle,
                    content.DownloadCover,
                    content.GenerateMovieMetadata,
                    content.PreferredSubtitleLanguage,
                    content.PreferredSubtitleType),
                settings.Video.FileNameParts?.ToImmutableArray() ?? [],
                settings.Video.FileNamePartTimeFormat ?? string.Empty,
                settings.Video.OrderFormat),
            new DanmakuApplicationSettings(
                settings.Danmaku.OutputFormat,
                settings.Danmaku.DanmakuTopFilter,
                settings.Danmaku.DanmakuBottomFilter,
                settings.Danmaku.DanmakuScrollFilter,
                settings.Danmaku.DanmakuRemoveEmojiAndSpecialCharacters,
                settings.Danmaku.DanmakuBlockedKeywords?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
                settings.Danmaku.DanmakuBlockedSenderUids?.ToImmutableArray() ?? ImmutableArray<long>.Empty,
                settings.Danmaku.IsCustomDanmakuResolution,
                settings.Danmaku.DanmakuScreenWidth,
                settings.Danmaku.DanmakuScreenHeight,
                settings.Danmaku.DanmakuFontName ?? string.Empty,
                settings.Danmaku.DanmakuFontSize,
                settings.Danmaku.DanmakuLineCount,
                settings.Danmaku.DanmakuLayoutAlgorithm),
            new AboutApplicationSettings(
                settings.About.IsReceiveBetaVersion,
                settings.About.AutoUpdateWhenLaunch,
                settings.About.SkipVersionOnLaunch ?? string.Empty),
            new UserApplicationSettings(
                settings.UserInfo.Mid,
                settings.UserInfo.Name ?? string.Empty,
                settings.UserInfo.IsLogin,
                settings.UserInfo.IsVip,
                settings.UserInfo.ImgKey ?? string.Empty,
                settings.UserInfo.SubKey ?? string.Empty),
            new WindowApplicationSettings(
                settings.WindowSettings.Width,
                settings.WindowSettings.Height,
                settings.WindowSettings.X,
                settings.WindowSettings.Y));
    }

    public static AppSettings ToModel(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new AppSettings
        {
            SchemaVersion = settings.SchemaVersion,
            Basic = new BasicSettings
            {
                ThemeMode = settings.Basic.ThemeMode,
                UiLanguage = settings.Basic.UiLanguage,
                AfterDownload = settings.Basic.AfterDownload,
                IsListenClipboard = settings.Basic.IsListenClipboard,
                DownloadFinishedSort = settings.Basic.DownloadFinishedSort,
                RepeatFileAutoAddNumberSuffix = settings.Basic.RepeatFileAutoAddNumberSuffix
            },
            Network = new NetworkSettings
            {
                IsLiftingOfRegion = settings.Network.IsLiftingOfRegion,
                UserAgent = settings.Network.UserAgent,
                HighSpeedDownloadMode = settings.Network.HighSpeedDownloadMode,
                NetworkProxy = settings.Network.NetworkProxy,
                CustomNetworkProxy = settings.Network.CustomNetworkProxy,
                MaxCurrentDownloads = settings.Network.MaxCurrentDownloads,
                Split = settings.Network.Split,
                MaxOverallDownloadLimit = settings.Network.MaxOverallDownloadLimit,
                MaxDownloadLimit = settings.Network.MaxDownloadLimit,
                IsHttpProxy = settings.Network.IsHttpProxy,
                HttpProxy = settings.Network.HttpProxy,
                HttpProxyListenPort = settings.Network.HttpProxyListenPort
            },
            Video = new VideoSettings
            {
                VideoCodecs = settings.Video.VideoCodecs,
                Quality = settings.Video.Quality,
                AudioQuality = settings.Video.AudioQuality,
                IsTranscodingFlvToMp4 = settings.Video.IsTranscodingFlvToMp4,
                IsTranscodingAacToMp3 = settings.Video.IsTranscodingAacToMp3,
                FfmpegHardwareAcceleration = settings.Video.FfmpegHardwareAcceleration,
                FfmpegMaxParallelJobs = settings.Video.FfmpegMaxParallelJobs,
                SaveVideoRootPath = settings.Video.SaveVideoRootPath,
                HistoryVideoRootPaths = settings.Video.HistoryVideoRootPaths,
                IsUseSaveVideoRootPath = settings.Video.IsUseSaveVideoRootPath,
                VideoContent = new VideoContentSettings
                {
                    DownloadAudio = settings.Video.Content.DownloadAudio,
                    DownloadVideo = settings.Video.Content.DownloadVideo,
                    DownloadDanmaku = settings.Video.Content.DownloadDanmaku,
                    DownloadSubtitle = settings.Video.Content.DownloadSubtitle,
                    DownloadCover = settings.Video.Content.DownloadCover,
                    GenerateMovieMetadata = settings.Video.Content.GenerateMovieMetadata,
                    PreferredSubtitleLanguage = settings.Video.Content.PreferredSubtitleLanguage,
                    PreferredSubtitleType = settings.Video.Content.PreferredSubtitleType
                },
                FileNameParts = settings.Video.FileNameParts,
                FileNamePartTimeFormat = settings.Video.FileNamePartTimeFormat,
                OrderFormat = settings.Video.OrderFormat
            },
            Danmaku = new DanmakuSettings
            {
                OutputFormat = settings.Danmaku.OutputFormat,
                DanmakuTopFilter = settings.Danmaku.TopFilter,
                DanmakuBottomFilter = settings.Danmaku.BottomFilter,
                DanmakuScrollFilter = settings.Danmaku.ScrollFilter,
                DanmakuRemoveEmojiAndSpecialCharacters = settings.Danmaku.RemoveEmojiAndSpecialCharacters,
                DanmakuBlockedKeywords = settings.Danmaku.BlockedKeywords,
                DanmakuBlockedSenderUids = settings.Danmaku.BlockedSenderUids,
                IsCustomDanmakuResolution = settings.Danmaku.IsCustomResolution,
                DanmakuScreenWidth = settings.Danmaku.ScreenWidth,
                DanmakuScreenHeight = settings.Danmaku.ScreenHeight,
                DanmakuFontName = settings.Danmaku.FontName,
                DanmakuFontSize = settings.Danmaku.FontSize,
                DanmakuLineCount = settings.Danmaku.LineCount,
                DanmakuLayoutAlgorithm = settings.Danmaku.LayoutAlgorithm
            },
            About = new AboutSettings
            {
                IsReceiveBetaVersion = settings.About.IsReceiveBetaVersion,
                AutoUpdateWhenLaunch = settings.About.AutoUpdateWhenLaunch,
                SkipVersionOnLaunch = settings.About.SkipVersionOnLaunch
            },
            UserInfo = new UserInfoSettings
            {
                Mid = settings.User.Mid,
                Name = settings.User.Name,
                IsLogin = settings.User.IsLogin,
                IsVip = settings.User.IsVip,
                ImgKey = settings.User.ImgKey,
                SubKey = settings.User.SubKey
            },
            WindowSettings = new WindowSettings
            {
                Width = settings.Window.Width,
                Height = settings.Window.Height,
                X = settings.Window.X,
                Y = settings.Window.Y
            }
        };
    }
}
