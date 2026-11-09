using System.Collections.Immutable;
using DownKyi.Application.Diagnostics;
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

    internal void UpdateSnapshot(Func<ApplicationSettings, ApplicationSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplicationSettings candidate;
        ImmutableArray<string> corrections;
        lock (_settingsLock)
        {
            candidate = update(CreateSnapshotCore())
                ?? throw new InvalidOperationException("A settings update cannot return null.");
            var validation = ApplicationSettingsValidator.Validate(candidate);
            candidate = validation.Settings;
            corrections = validation.Corrections;
            ReplaceSnapshotUnsafe(candidate);
        }

        LogCorrections(corrections);
        Changed?.Invoke();
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
        lock (_settingsLock)
        {
            ReplaceSnapshotUnsafe(validated);
        }

        Changed?.Invoke();
    }

    private void ReplaceSnapshotUnsafe(ApplicationSettings snapshot)
    {
        var replacement = SettingsSnapshotModelMapper.ToModel(snapshot);
        var replacementJson = JsonConvert.SerializeObject(replacement);
        var currentJson = JsonConvert.SerializeObject(_appSettings);
        if (!string.Equals(currentJson, replacementJson, StringComparison.Ordinal))
        {
            _appSettings = replacement;
            MarkDirtyUnsafe();
        }
    }

    private void LogCorrections(ImmutableArray<string> corrections)
    {
        if (corrections.Length > 0)
        {
            _logger.LogInformationMessage(
                $"Invalid settings were restored to safe defaults: {string.Join(", ", corrections)}");
        }
    }
}
