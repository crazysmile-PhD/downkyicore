using System.Collections.Immutable;
using DownKyi.Application.Settings;
using Newtonsoft.Json;

namespace DownKyi.Infrastructure.Settings;

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
                GetDownloadFinishedSort(),
                IsRepeatFileAutoAddNumberSuffix())
            {
                UiLanguage = UiLanguage
            },
            new NetworkApplicationSettings(
                GetIsLiftingOfRegion(),
                GetUserAgent(),
                GetHighSpeedDownloadMode(),
                GetNetworkProxy(),
                GetCustomProxy(),
                GetMaxCurrentDownloads(),
                GetSplit(),
                GetMaxOverallDownloadLimit(),
                GetMaxDownloadLimit(),
                GetIsHttpProxy(),
                GetHttpProxy(),
                GetHttpProxyListenPort()),
            new VideoApplicationSettings(
                GetVideoCodecs(),
                GetQuality(),
                GetAudioQuality(),
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
                    content.GenerateMovieMetadata,
                    content.PreferredSubtitleLanguage,
                    content.PreferredSubtitleType),
                GetFileNameParts().ToImmutableArray(),
                GetFileNamePartTimeFormat(),
                GetOrderFormat()),
            new DanmakuApplicationSettings(
                GetDanmakuOutputFormat(),
                GetDanmakuTopFilter(),
                GetDanmakuBottomFilter(),
                GetDanmakuScrollFilter(),
                GetDanmakuRemoveEmojiAndSpecialCharacters(),
                GetDanmakuBlockedKeywords().ToImmutableArray(),
                GetDanmakuBlockedSenderUids().ToImmutableArray(),
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
        var replacement = SettingsSnapshotModelMapper.ToModel(validated);
        var replacementJson = JsonConvert.SerializeObject(replacement);
        lock (_settingsLock)
        {
            var currentJson = JsonConvert.SerializeObject(_appSettings);
            if (!string.Equals(currentJson, replacementJson, StringComparison.Ordinal))
            {
                _appSettings = replacement;
                MarkDirtyUnsafe();
            }
        }

        Changed?.Invoke();
    }
}
