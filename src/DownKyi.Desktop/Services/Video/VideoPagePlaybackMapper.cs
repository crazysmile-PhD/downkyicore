using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Utils;
using DownKyi.Presentation;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Video;

internal static class VideoPagePlaybackMapper
{
    /// <summary>
    /// 从视频流更新VideoPage
    /// </summary>
    /// <param name="playUrl"></param>
    /// <param name="page"></param>
    internal static void ApplyPlayUrl(
        PlayUrl? playUrl,
        VideoPage page,
        ApplicationSettings settings,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (playUrl == null)
        {
            return;
        }

        var availability = playUrl.Availability ?? PlayUrlAvailability.From(playUrl);
        page.PlaybackAvailability = availability;
        if (playUrl.Diagnostics != null)
        {
            logger?.LogInformationMessage(BuildCapabilitySummary(playUrl, settings));
        }

        // 获取设置
        var defaultQuality = settings.Video.Quality;
        var videoCodecs = settings.Video.VideoCodecs;
        var defaultAudioQuality = settings.Video.AudioQuality;

        var audioMatch = PlaybackQualityCatalog.SelectAudioQuality(
            availability.Audio,
            defaultAudioQuality);
        var selectedAudioQuality = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(quality => quality.Id == audioMatch.SelectedQuality)
            ?.Name ?? string.Empty;
        page.SetAutomaticAudioQuality(selectedAudioQuality, audioMatch);

        var videoQualities = GetVideoQualityList(availability.Video, videoCodecs);
        var videoMatch = PlaybackQualityCatalog.SelectVideoQuality(
            videoQualities.Select(quality => quality.Quality),
            defaultQuality);
        page.SetAutomaticVideoQuality(
            videoQualities.FirstOrDefault(quality =>
                quality.Quality == videoMatch.SelectedQuality),
            videoMatch);

        var playbackDuration = playUrl.Dash.Duration > 0
            ? playUrl.Dash.Duration
            : playUrl.Durl.Select(item => item.Length).Sum() / 1000;
        if (playbackDuration > 0)
        {
            page.Duration = VideoPageDuration.FromSeconds(playbackDuration);
        }
    }

    /// <summary>
    /// 设置画质 & 视频编码
    /// </summary>
    /// <param name="playUrl"></param>
    /// <param name="videoCodecs"></param>
    /// <returns></returns>
    private static List<VideoQuality> GetVideoQualityList(
        IEnumerable<PlayUrlVideoAvailability> availableVideo,
        int videoCodecs)
    {
        var codeIds = PlaybackQualityCatalog.GetCodecIds();
        var resolutions = PlaybackQualityCatalog.GetResolutions();
        return availableVideo
            .GroupBy(video => (video.Quality, IsDurl: video.StreamKind == PlayUrlStreamKind.Durl))
            .Select(group =>
            {
                var first = group.First();
                var codecNames = group
                    .Select(video => codeIds.FirstOrDefault(codec => codec.Id == video.CodecId)?.Name)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var preferredCodecName = codeIds.FirstOrDefault(codec => codec.Id == videoCodecs)?.Name;
                var selectedCodecName = codecNames.Contains(preferredCodecName, StringComparer.Ordinal)
                    ? preferredCodecName
                    : codeIds.TakeWhile(codec => codec.Id != videoCodecs)
                        .Where(codec => codecNames.Contains(codec.Name, StringComparer.Ordinal))
                        .Select(codec => codec.Name)
                        .LastOrDefault() ?? codecNames.FirstOrDefault();
                return new VideoQuality
                {
                    Quality = group.Key.Quality,
                    IsDurl = group.Key.IsDurl,
                    QualityFormat = string.IsNullOrWhiteSpace(first.Description)
                        ? resolutions.FirstOrDefault(resolution => resolution.Id == first.Quality)?.Name
                          ?? string.Empty
                        : first.Description,
                    SelectedVideoCodec = selectedCodecName ?? string.Empty
                };
            })
            .OrderByDescending(quality => quality.Quality)
            .ToList();
    }

    internal static string BuildCapabilitySummary(
        PlayUrl playUrl,
        ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(playUrl);
        ArgumentNullException.ThrowIfNull(settings);
        var supportQualities = string.Join(",", (playUrl.SupportFormats ?? []).Select(format =>
            $"{format.Quality}(login={FormatNullableBoolean(format.NeedLogin)},vip={FormatNullableBoolean(format.NeedVip)})"));
        var dashVideoIds = string.Join(",", playUrl.Dash.Video
            .Select(video => video.Id)
            .Distinct()
            .OrderByDescending(id => id));
        var dashVideoCodecIds = string.Join(",", playUrl.Dash.Video
            .Select(video => video.CodecId)
            .Distinct()
            .OrderBy(id => id));
        var availableVideo = string.Join(",", (playUrl.Availability ?? PlayUrlAvailability.From(playUrl)).Video
            .Select(video => $"{video.Quality}:{video.CodecId}:{video.StreamKind}"));
        var diagnostics = playUrl.Diagnostics;

        return "Playback capabilities. "
               + $"configuredQuality={settings.Video.Quality}; "
               + $"isLogin={settings.User.IsLogin}; "
               + $"isVip={settings.User.IsVip}; "
               + $"requestedQuality={diagnostics?.RequestedQuality.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; "
               + $"fnval={(diagnostics?.Fnval is { } fnval ? fnval.ToString(CultureInfo.InvariantCulture) : "unknown")}; "
               + $"playDetail={SanitizeDiagnosticToken(diagnostics?.PlayDetail)}; "
               + $"playbackSource={diagnostics?.Source.ToString() ?? "unknown"}; "
               + $"resolution={SanitizeDiagnosticToken(diagnostics?.Outcome)}; "
               + $"returnedQuality={playUrl.Quality}; "
               + $"isPreview={playUrl.IsPreview?.ToString() ?? "unknown"}; "
               + $"protection=[{BuildProtectionSummary(playUrl)}]; "
               + $"acceptQuality=[{string.Join(",", playUrl.AcceptQuality ?? [])}]; "
               + $"supportQuality=[{supportQualities}]; "
               + $"dashVideoIds=[{dashVideoIds}]; "
               + $"dashVideoCodecIds=[{dashVideoCodecIds}]; "
               + $"availableVideo=[{availableVideo}]";
    }

    private static string BuildProtectionSummary(PlayUrl playUrl)
    {
        return playUrl.Diagnostics?.Protection is { Count: > 0 } protection
            ? string.Join(";", protection.Select(info => FormatProtection(info, info.Source.ToString())))
            : FormatProtection(PlayUrlProtectionDiagnostics.From(playUrl, default), "Payload");
    }

    private static string FormatProtection(PlayUrlProtectionDiagnostics info, string source) =>
        $"{source}(isDrm={FormatNullableBoolean(info.IsDrm)},"
        + $"drmTechType={info.DrmTechType?.ToString(CultureInfo.InvariantCulture) ?? "unknown"},"
        + $"widevineVideo=[{string.Join(",", info.WidevineVideoIds)}],"
        + $"widevineAudio=[{string.Join(",", info.WidevineAudioIds)}])";

    private static string FormatNullableBoolean(bool? value)
    {
        return value?.ToString() ?? "unknown";
    }

    private static string SanitizeDiagnosticToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        var sanitized = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or ':')
            .ToArray());
        return string.IsNullOrEmpty(sanitized) ? "unknown" : sanitized;
    }
}
