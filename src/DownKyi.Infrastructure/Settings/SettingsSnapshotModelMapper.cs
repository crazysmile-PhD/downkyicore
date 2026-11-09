using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings.Models;

namespace DownKyi.Infrastructure.Settings;

internal static class SettingsSnapshotModelMapper
{
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
