using System.Collections.Immutable;
using DownKyi.Core.Settings.Models;

namespace DownKyi.Core.Settings;

public partial class SettingsManager
{
    internal ApplicationSettings CreateSnapshot(bool persistDefaultValues = true)
    {
        lock (_settingsLock)
        {
            _suppressChangeNotifications++;
            if (!persistDefaultValues)
            {
                _suppressPersistence++;
            }

            try
            {
                return CreateSnapshotCore();
            }
            finally
            {
                if (!persistDefaultValues)
                {
                    _suppressPersistence--;
                }

                _suppressChangeNotifications--;
            }
        }
    }

    internal ApplicationSettings CreateInitialSnapshot()
    {
        return CreateSnapshot(_persistInitialDefaults);
    }

    private ApplicationSettings CreateSnapshotCore()
    {
        var content = GetVideoContent();
        var user = GetUserInfo();
        var window = GetWindowSettings();
        return new ApplicationSettings(
            _appSettings.SchemaVersion,
            new BasicApplicationSettings(
                GetThemeMode(),
                GetAfterDownloadOperation(),
                GetIsListenClipboard(),
                GetIsAutoParseVideo(),
                GetParseScope(),
                GetIsAutoDownloadAll(),
                GetDownloadFinishedSort(),
                GetRepeatDownloadStrategy(),
                IsRepeatFileAutoAddNumberSuffix()),
            new NetworkApplicationSettings(
                GetIsLiftingOfRegion(),
                GetUseSsl(),
                GetUserAgent(),
                GetDownloader(),
                GetHighSpeedDownloadMode(),
                GetNetworkProxy(),
                GetCustomProxy(),
                GetMaxCurrentDownloads(),
                GetSplit(),
                GetIsHttpProxy(),
                GetHttpProxy(),
                GetHttpProxyListenPort(),
                GetAriaToken(),
                GetAriaHost(),
                GetAriaListenPort(),
                GetAriaLogLevel(),
                GetAriaSplit(),
                GetAriaMaxConnectionPerServer(),
                GetAriaMinSplitSize(),
                GetAriaMaxOverallDownloadLimit(),
                GetAriaMaxDownloadLimit(),
                GetAriaFileAllocation(),
                GetIsAriaHttpProxy(),
                GetAriaHttpProxy(),
                GetAriaHttpProxyListenPort()),
            new VideoApplicationSettings(
                GetVideoCodecs(),
                GetQuality(),
                GetAudioQuality(),
                VideoParseType,
                GetIsTranscodingFlvToMp4(),
                GetIsTranscodingAacToMp3(),
                GetFfmpegHardwareAcceleration(),
                GetFfmpegMaxParallelJobs(),
                GetSaveVideoRootPath(),
                GetHistoryVideoRootPaths().ToImmutableArray(),
                GetIsUseSaveVideoRootPath(),
                new VideoContentApplicationSettings(
                    content.DownloadAudio,
                    content.DownloadVideo,
                    content.DownloadDanmaku,
                    content.DownloadSubtitle,
                    content.DownloadCover,
                    content.GenerateMovieMetadata),
                GetFileNameParts().ToImmutableArray(),
                GetFileNamePartTimeFormat(),
                GetOrderFormat()),
            new DanmakuApplicationSettings(
                GetDanmakuTopFilter(),
                GetDanmakuBottomFilter(),
                GetDanmakuScrollFilter(),
                GetIsCustomDanmakuResolution(),
                GetDanmakuScreenWidth(),
                GetDanmakuScreenHeight(),
                GetDanmakuFontName(),
                GetDanmakuFontSize(),
                GetDanmakuLineCount(),
                GetDanmakuLayoutAlgorithm()),
            new AboutApplicationSettings(
                GetIsReceiveBetaVersion(),
                GetAutoUpdateWhenLaunch(),
                GetSkipVersionOnLaunch()),
            new UserApplicationSettings(
                user.Mid,
                user.Name,
                user.IsLogin,
                user.IsVip,
                user.ImgKey,
                user.SubKey),
            new WindowApplicationSettings(window.Width, window.Height, window.X, window.Y));

    }

    internal void ApplySnapshot(ApplicationSettings snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var validated = ApplicationSettingsValidator.Validate(snapshot).Settings;
        lock (_settingsLock)
        {
            _suppressChangeNotifications++;
            try
            {
                SetProperty(_appSettings.SchemaVersion, validated.SchemaVersion, value => _appSettings.SchemaVersion = value);
                SetThemeMode(validated.Basic.ThemeMode);
                SetAfterDownloadOperation(validated.Basic.AfterDownload);
                SetIsListenClipboard(validated.Basic.IsListenClipboard);
                SetIsAutoParseVideo(validated.Basic.IsAutoParseVideo);
                SetParseScope(validated.Basic.ParseScope);
                SetIsAutoDownloadAll(validated.Basic.IsAutoDownloadAll);
                SetDownloadFinishedSort(validated.Basic.DownloadFinishedSort);
                SetRepeatDownloadStrategy(validated.Basic.RepeatDownloadStrategy);
                IsRepeatFileAutoAddNumberSuffix(validated.Basic.RepeatFileAutoAddNumberSuffix);

                SetIsLiftingOfRegion(validated.Network.IsLiftingOfRegion);
                SetUseSsl(validated.Network.UseSsl);
                SetUserAgent(validated.Network.UserAgent);
                SetDownloader(validated.Network.Downloader);
                SetHighSpeedDownloadMode(validated.Network.HighSpeedDownloadMode);
                SetNetworkProxy(validated.Network.NetworkProxy);
                SetProperty(
                    _appSettings.Network.CustomNetworkProxy,
                    validated.Network.CustomNetworkProxy,
                    value => _appSettings.Network.CustomNetworkProxy = value);
                SetMaxCurrentDownloads(validated.Network.MaxCurrentDownloads);
                SetSplit(validated.Network.Split);
                SetIsHttpProxy(validated.Network.IsHttpProxy);
                SetHttpProxy(validated.Network.HttpProxy);
                SetHttpProxyListenPort(validated.Network.HttpProxyListenPort);
                SetAriaToken(validated.Network.AriaToken);
                SetAriaHost(validated.Network.AriaHost);
                SetAriaListenPort(validated.Network.AriaListenPort);
                SetAriaLogLevel(validated.Network.AriaLogLevel);
                SetAriaSplit(validated.Network.AriaSplit);
                SetAriaMaxConnectionPerServer(validated.Network.AriaMaxConnectionPerServer);
                SetAriaMinSplitSize(validated.Network.AriaMinSplitSize);
                SetAriaMaxOverallDownloadLimit(validated.Network.AriaMaxOverallDownloadLimit);
                SetAriaMaxDownloadLimit(validated.Network.AriaMaxDownloadLimit);
                SetAriaFileAllocation(validated.Network.AriaFileAllocation);
                SetIsAriaHttpProxy(validated.Network.IsAriaHttpProxy);
                SetAriaHttpProxy(validated.Network.AriaHttpProxy);
                SetAriaHttpProxyListenPort(validated.Network.AriaHttpProxyListenPort);

                SetVideoCodecs(validated.Video.VideoCodecs);
                SetQuality(validated.Video.Quality);
                SetAudioQuality(validated.Video.AudioQuality);
                SetVideoParseType(validated.Video.VideoParseType);
                SetIsTranscodingFlvToMp4(validated.Video.IsTranscodingFlvToMp4);
                SetIsTranscodingAacToMp3(validated.Video.IsTranscodingAacToMp3);
                SetFfmpegHardwareAcceleration(validated.Video.FfmpegHardwareAcceleration);
                SetFfmpegMaxParallelJobs(validated.Video.FfmpegMaxParallelJobs);
                SetSaveVideoRootPath(validated.Video.SaveVideoRootPath);
                SetHistoryVideoRootPaths(validated.Video.HistoryVideoRootPaths);
                SetIsUseSaveVideoRootPath(validated.Video.IsUseSaveVideoRootPath);
                SetVideoContent(new VideoContentSettings
                {
                    DownloadAudio = validated.Video.Content.DownloadAudio,
                    DownloadVideo = validated.Video.Content.DownloadVideo,
                    DownloadDanmaku = validated.Video.Content.DownloadDanmaku,
                    DownloadSubtitle = validated.Video.Content.DownloadSubtitle,
                    DownloadCover = validated.Video.Content.DownloadCover,
                    GenerateMovieMetadata = validated.Video.Content.GenerateMovieMetadata
                });
                SetFileNameParts(validated.Video.FileNameParts);
                SetFileNamePartTimeFormat(validated.Video.FileNamePartTimeFormat);
                SetOrderFormat(validated.Video.OrderFormat);

                SetDanmakuTopFilter(validated.Danmaku.TopFilter);
                SetDanmakuBottomFilter(validated.Danmaku.BottomFilter);
                SetDanmakuScrollFilter(validated.Danmaku.ScrollFilter);
                SetIsCustomDanmakuResolution(validated.Danmaku.IsCustomResolution);
                SetDanmakuScreenWidth(validated.Danmaku.ScreenWidth);
                SetDanmakuScreenHeight(validated.Danmaku.ScreenHeight);
                SetDanmakuFontName(validated.Danmaku.FontName);
                SetDanmakuFontSize(validated.Danmaku.FontSize);
                SetDanmakuLineCount(validated.Danmaku.LineCount);
                SetDanmakuLayoutAlgorithm(validated.Danmaku.LayoutAlgorithm);

                SetIsReceiveBetaVersion(validated.About.IsReceiveBetaVersion);
                SetAutoUpdateWhenLaunch(validated.About.AutoUpdateWhenLaunch);
                SetProperty(
                    _appSettings.About.SkipVersionOnLaunch,
                    validated.About.SkipVersionOnLaunch,
                    value => _appSettings.About.SkipVersionOnLaunch = value);
                SetUserInfo(new UserInfoSettings
                {
                    Mid = validated.User.Mid,
                    Name = validated.User.Name,
                    IsLogin = validated.User.IsLogin,
                    IsVip = validated.User.IsVip,
                    ImgKey = validated.User.ImgKey,
                    SubKey = validated.User.SubKey
                });
                SettingWindowSettings(new WindowSettings
                {
                    Width = validated.Window.Width,
                    Height = validated.Window.Height,
                    X = validated.Window.X,
                    Y = validated.Window.Y
                });
            }
            finally
            {
                _suppressChangeNotifications--;
            }
        }

        Changed?.Invoke();
    }
}
