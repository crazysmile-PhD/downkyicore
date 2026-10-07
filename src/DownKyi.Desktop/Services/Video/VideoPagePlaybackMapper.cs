using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
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

        // 视频流信息
        page.PlayUrl = playUrl;
        if (playUrl.Diagnostics != null)
        {
            logger?.LogInformationMessage(BuildCapabilitySummary(playUrl, settings));
        }

        // 获取设置
        var defaultQuality = settings.Video.Quality;
        var videoCodecs = settings.Video.VideoCodecs;
        var defaultAudioQuality = settings.Video.AudioQuality;

        if (playUrl.Dash?.Video is { Count: > 0 })
        {
            // 音质
            page.AudioQualityFormatList = GetAudioQualityFormatList(playUrl);
            if (page.AudioQualityFormatList.Count > 0)
            {
                page.AudioQualityFormat = SelectPreferredAudioQuality(
                    page.AudioQualityFormatList,
                    defaultAudioQuality);
            }

            // 画质 & 视频编码
            page.VideoQualityList = GetVideoQualityList(playUrl, videoCodecs);
            if (page.VideoQualityList.Count > 0)
            {
                page.VideoQuality = SelectPreferredVideoQuality(
                    page.VideoQualityList,
                    defaultQuality);
            }

            // 时长
            page.Duration = Format.FormatDuration(playUrl.Dash.Duration);

            return;
        }


        if (playUrl.Durl?.Count > 0)
        {
            var codeIds = PlaybackQualityCatalog.GetCodecIds();
            var qns = PlaybackQualityCatalog.GetResolutions();
            var quality = new VideoQuality
            {
                Quality = playUrl.Quality,
                QualityFormat = qns.First(x => x.Id == playUrl.Quality).Name,
                VideoCodecList = codeIds.Where(x => x.Id == playUrl.VideoCodecid)
                    .Select(x => x.Name)
                    .ToList(),
                SelectedVideoCodec = codeIds.First(x => x.Id == playUrl.VideoCodecid).Name
            };

            page.VideoQualityList = new List<VideoQuality> { quality };
            page.VideoQuality = page.VideoQualityList[0];
            page.Duration = Format.FormatDuration(playUrl.Durl.Select(x => x.Length).Sum() / 1000);
            return;
        }
    }

    /// <summary>
    /// 设置音质
    /// </summary>
    /// <param name="playUrl"></param>
    /// <returns></returns>
    private static ObservableCollection<string> GetAudioQualityFormatList(PlayUrl playUrl)
    {
        var audioQualityFormatList = new List<string>();
        var sortList = new List<string>();
        var audioQualities = PlaybackQualityCatalog.GetAudioQualities();

        if (playUrl.Dash.Audio != null && playUrl.Dash.Audio.Count > 0)
        {
            foreach (var audio in playUrl.Dash.Audio)
            {
                var audioQuality = audioQualities.FirstOrDefault(t => { return t.Id == audio.Id; });
                if (audioQuality != null)
                {
                    ListHelper.AddUnique(audioQualityFormatList, audioQuality.Name);
                }
            }
        }

        if (playUrl.Dash.Dolby != null)
        {
            if (playUrl.Dash.Dolby.Audio != null && playUrl.Dash.Dolby.Audio.Count > 0)
            {
                ListHelper.AddUnique(audioQualityFormatList, audioQualities[3].Name);
            }
        }

        if (playUrl.Dash.Flac != null)
        {
            if (playUrl.Dash.Flac.Audio != null)
            {
                ListHelper.AddUnique(audioQualityFormatList, audioQualities[4].Name);
            }
        }

        foreach (var item in audioQualities)
        {
            if (audioQualityFormatList.Contains(item.Name))
            {
                sortList.Add(item.Name);
            }
        }

        sortList.Reverse();

        return new ObservableCollection<string>(sortList);
    }

    private static string SelectPreferredAudioQuality(
        IReadOnlyCollection<string> availableQualities,
        int preferredQuality)
    {
        var catalog = PlaybackQualityCatalog.GetAudioQualities()
            .Select((quality, index) => new
            {
                quality.Name,
                PreferredId = index >= 3 ? quality.Id + 1000 : quality.Id
            })
            .Where(quality => availableQualities.Contains(quality.Name))
            .OrderByDescending(quality => quality.PreferredId)
            .ToArray();
        var selected = catalog.FirstOrDefault(quality => quality.PreferredId == preferredQuality)
                       ?? catalog.FirstOrDefault(quality => quality.PreferredId <= preferredQuality)
                       ?? catalog.First();
        return selected.Name;
    }

    /// <summary>
    /// 设置画质 & 视频编码
    /// </summary>
    /// <param name="playUrl"></param>
    /// <param name="videoCodecs"></param>
    /// <returns></returns>
    private static List<VideoQuality> GetVideoQualityList(PlayUrl playUrl, int videoCodecs)
    {
        var videoQualityList = new List<VideoQuality>();
        var codeIds = PlaybackQualityCatalog.GetCodecIds();

        if (playUrl.Dash.Video == null)
        {
            return videoQualityList;
        }

        foreach (var video in playUrl.Dash.Video)
        {
            var qualityFormat = string.Empty;
            var selectedQuality = playUrl.SupportFormats.FirstOrDefault(t => t.Quality == video.Id);
            if (selectedQuality != null)
            {
                qualityFormat = selectedQuality.NewDescription;
            }

            // 寻找是否已存在这个画质
            // 不存在则添加，存在则修改
            var codecName = codeIds.FirstOrDefault(t => t.Id == video.CodecId)?.Name ?? string.Empty;
            var videoQualityExist = videoQualityList.FirstOrDefault(t => t.Quality == video.Id);
            if (videoQualityExist == null)
            {
                var videoCodecList = new List<string>();
                if (!string.IsNullOrEmpty(codecName))
                {
                    ListHelper.AddUnique(videoCodecList, codecName);
                }

                var videoQuality = new VideoQuality
                {
                    Quality = video.Id,
                    QualityFormat = qualityFormat,
                    VideoCodecList = videoCodecList
                };
                videoQualityList.Add(videoQuality);
            }
            else
            {
                if (!videoQualityExist.VideoCodecList.Any(t => t.Equals(codecName, System.StringComparison.Ordinal)))
                {
                    if (!string.IsNullOrEmpty(codecName))
                    {
                        videoQualityExist.VideoCodecList.Add(codecName);
                    }
                }
            }

            // 设置选中的视频编码
            var selectedVideoQuality = videoQualityList.FirstOrDefault(t => t.Quality == video.Id);
            if (selectedVideoQuality == null)
            {
                continue;
            }

            // 设置选中的视频编码
            var videoCodecsName = codeIds.FirstOrDefault(t => t.Id == videoCodecs)?.Name ?? string.Empty;
            if (videoQualityList[videoQualityList.IndexOf(selectedVideoQuality)].VideoCodecList.Contains(videoCodecsName))
            {
                videoQualityList[videoQualityList.IndexOf(selectedVideoQuality)].SelectedVideoCodec = videoCodecsName;
            }
            else
            {
                // 当获取的视频没有设置的视频编码时
                foreach (var codec in codeIds)
                {
                    if (videoQualityList[videoQualityList.IndexOf(selectedVideoQuality)].VideoCodecList
                        .Contains(codec.Name))
                    {
                        videoQualityList[videoQualityList.IndexOf(selectedVideoQuality)].SelectedVideoCodec =
                            codec.Name;
                    }

                    if (codec.Id == videoCodecs)
                    {
                        break;
                    }
                }

                // 若默认编码为AVC，但画质为杜比视界时，
                // 上面的foreach不会选中HEVC编码，
                // 而杜比视界只有HEVC编码，
                // 因此这里再判断并设置一次
                if (string.IsNullOrEmpty(selectedVideoQuality.SelectedVideoCodec) && selectedVideoQuality.VideoCodecList.Count > 0)
                {
                    selectedVideoQuality.SelectedVideoCodec = selectedVideoQuality.VideoCodecList[0];
                }
            }
        }

        return videoQualityList
            .OrderByDescending(quality => quality.Quality)
            .ToList();
    }

    private static VideoQuality SelectPreferredVideoQuality(
        IEnumerable<VideoQuality> availableQualities,
        int preferredQuality)
    {
        var qualities = availableQualities.ToArray();
        return qualities.FirstOrDefault(quality => quality.Quality == preferredQuality)
               ?? qualities
                   .Where(quality => quality.Quality <= preferredQuality)
                   .MaxBy(quality => quality.Quality)
               ?? qualities.MaxBy(quality => quality.Quality)!;
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
        var diagnostics = playUrl.Diagnostics;

        return "Playback capabilities. "
               + $"configuredQuality={settings.Video.Quality}; "
               + $"isLogin={settings.User.IsLogin}; "
               + $"isVip={settings.User.IsVip}; "
               + $"requestedQuality={diagnostics?.RequestedQuality.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; "
               + $"fnval={diagnostics?.Fnval.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; "
               + $"playDetail={SanitizeDiagnosticToken(diagnostics?.PlayDetail)}; "
               + $"fallback={SanitizeDiagnosticToken(diagnostics?.FallbackOutcome)}; "
               + $"usedWebPageFallback={diagnostics?.UsedWebPageFallback.ToString() ?? "unknown"}; "
               + $"returnedQuality={playUrl.Quality}; "
               + $"isPreview={playUrl.IsPreview?.ToString() ?? "unknown"}; "
               + $"acceptQuality=[{string.Join(",", playUrl.AcceptQuality ?? [])}]; "
               + $"supportQuality=[{supportQualities}]; "
               + $"dashVideoIds=[{dashVideoIds}]; "
               + $"dashVideoCodecIds=[{dashVideoCodecIds}]";
    }

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
