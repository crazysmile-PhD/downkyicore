using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Presentation;
using DownKyi.Services.Video;

namespace DownKyi.Tests;

public sealed class VideoPagePlaybackMapperTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-video-page-playback-mapper-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplyPlayUrlFallsBackToHevcWhenAvcIsUnavailableAtHighestQuality()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                Quality = 127,
                VideoCodecs = 7
            },
            User = settingsStore.Current.User with
            {
                Mid = 1,
                IsLogin = true,
                IsVip = true
            }
        };
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 126,
                        CodecId = 12,
                        BaseAddress = "https://media.invalid/video-126"
                    },
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video-80"
                    }
                ]
            },
            SupportFormats =
            [
                new PlayUrlSupportFormat { Quality = 126, NewDescription = "杜比视界" },
                new PlayUrlSupportFormat { Quality = 80, NewDescription = "1080P 高清" }
            ]
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(126, page.VideoQuality!.Quality);
        Assert.Equal("杜比视界", page.VideoQuality!.QualityFormat);
        Assert.Equal("H.265/HEVC", page.VideoQuality!.SelectedVideoCodec);
    }

    [Fact]
    public void ApplyPlayUrlPreservesHevcFallbackWhenAv1IsUnavailable()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                Quality = 127,
                VideoCodecs = 13
            },
            User = settingsStore.Current.User with
            {
                Mid = 1,
                IsLogin = true,
                IsVip = true
            }
        };
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video-80-avc"
                    },
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 12,
                        BaseAddress = "https://media.invalid/video-80-hevc"
                    }
                ]
            },
            SupportFormats =
            [
                new PlayUrlSupportFormat { Quality = 80, NewDescription = "1080P 高清" }
            ]
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(80, page.VideoQuality!.Quality);
        Assert.Equal("H.265/HEVC", page.VideoQuality!.SelectedVideoCodec);
    }

    [Fact]
    public void Preferred720PDoesNotHideHigherAvailableQualities()
    {
        var settings = CreateSettings(videoQuality: 64, isVip: true);
        var playUrl = CreatePlayUrl(120, 112, 80, 64);
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal([120, 112, 80, 64], page.VideoQualityList.Select(quality => quality.Quality));
        Assert.Equal(64, page.VideoQuality!.Quality);
    }

    [Fact]
    public void Preferred720PRemainsSelectedAfterHigherFallbackQualityIsMerged()
    {
        var settings = CreateSettings(videoQuality: 64, isVip: true);
        var page = new VideoPage();
        var playUrl = CreatePlayUrl(112, 64);

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(
            [112, 64],
            page.VideoQualityList.Select(quality => quality.Quality));
        Assert.Equal(64, page.VideoQuality!.Quality);
    }

    [Fact]
    public void HigherOnlyFallbackQualityRequiresAnExplicitUserSelection()
    {
        var settings = CreateSettings(videoQuality: 80, isVip: true);
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(CreatePlayUrl(112), page, settings);

        Assert.Equal(112, Assert.Single(page.VideoQualityList).Quality);
        Assert.Null(page.VideoQuality);
    }

    [Fact]
    public void DurlPlaybackIsMappedWhenDashContainerIsEmpty()
    {
        var settings = CreateSettings(videoQuality: 112, isVip: true);
        var playUrl = new PlayUrl
        {
            Quality = 112,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://media.invalid/video-112.flv"
                }
            ],
            Dash = new PlayUrlDash()
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(112, Assert.Single(page.VideoQualityList).Quality);
        Assert.Equal(112, page.VideoQuality!.Quality);
        Assert.Equal("H.264/AVC", page.VideoQuality!.SelectedVideoCodec);
        Assert.True(page.VideoQuality!.IsDurl);
    }

    [Fact]
    public void AvailabilityMapsDashAndDurlWithoutMixingTheirManifests()
    {
        var settings = CreateSettings(videoQuality: 112, isVip: true);
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 64,
                        CodecId = 7,
                        BaseAddress = "https://api.invalid/video-64"
                    }
                ]
            },
            Availability = new PlayUrlAvailability(
            [
                new PlayUrlVideoAvailability(
                    112,
                    7,
                    PlayUrlStreamKind.Durl,
                    "1080P 高码率"),
                new PlayUrlVideoAvailability(
                    64,
                    7,
                    PlayUrlStreamKind.Dash,
                    "720P 高清")
            ],
            [30280])
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal([112, 64], page.VideoQualityList.Select(quality => quality.Quality));
        Assert.Equal(112, page.VideoQuality!.Quality);
        Assert.True(page.VideoQuality!.IsDurl);
        Assert.Empty(playUrl.Durl);
        Assert.Equal(64, Assert.Single(playUrl.Dash.Video).Id);
    }

    [Fact]
    public void EmptyAvailabilityDoesNotFallBackToUnverifiedRawStreams()
    {
        var settings = CreateSettings(videoQuality: 112, isVip: true);
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo { Id = 112, CodecId = 13 }]
            },
            Availability = new PlayUrlAvailability([], [])
        };
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Empty(page.VideoQualityList);
    }

    [Fact]
    public void MissingPreferredQualitySelectsHighestAvailableQualityBelowIt()
    {
        var settings = CreateSettings(videoQuality: 116, isVip: true);
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(CreatePlayUrl(112, 80, 64), page, settings);

        Assert.Equal(112, page.VideoQuality!.Quality);
    }

    [Fact]
    public void ServerProvidedVipQualityIsNotHiddenByCachedAccountState()
    {
        var settings = CreateSettings(videoQuality: 127, isVip: false);
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(CreatePlayUrl(112), page, settings);

        Assert.Equal(112, Assert.Single(page.VideoQualityList).Quality);
        Assert.Equal(112, page.VideoQuality!.Quality);
    }

    [Fact]
    public void AudioPreferenceSelectsWithoutFilteringHigherAvailableQualities()
    {
        var baseline = CreateSettings(videoQuality: 80, isVip: true);
        var settings = baseline with
        {
            Video = baseline.Video with
            {
                AudioQuality = 30232
            }
        };
        var playUrl = CreatePlayUrl(80);
        playUrl.Dash.Audio =
        [
            new PlayUrlDashVideo
            {
                Id = 30280,
                BaseAddress = "https://media.invalid/audio-30280"
            },
            new PlayUrlDashVideo
            {
                Id = 30232,
                BaseAddress = "https://media.invalid/audio-30232"
            },
            new PlayUrlDashVideo
            {
                Id = 30216,
                BaseAddress = "https://media.invalid/audio-30216"
            }
        ];
        var page = new VideoPage();

        VideoPagePlaybackMapper.ApplyPlayUrl(playUrl, page, settings);

        Assert.Equal(["高质量", "中质量", "低质量"], page.AudioQualityFormatList);
        Assert.Equal("中质量", page.AudioQualityFormat);
    }

    [Fact]
    public void CapabilitySummaryContainsOnlySanitizedPlaybackMetadata()
    {
        var settings = CreateSettings(videoQuality: 64, isVip: true);
        var playUrl = CreatePlayUrl(64);
        playUrl.AcceptQuality = [112, 80, 64];
        playUrl.SupportFormats =
        [
            new PlayUrlSupportFormat
            {
                Quality = 112,
                NeedLogin = true,
                NeedVip = true
            }
        ];
        playUrl.Dash.Video =
        [
            new PlayUrlDashVideo
            {
                Id = 64,
                CodecId = 7,
                BaseAddress = "https://media.invalid/private-token"
            }
        ];
        playUrl.Diagnostics = new PlayUrlDiagnostics(
            127,
            4048,
            "PLAY_WHOLE\r\nforged",
            false,
            "embedded-playback-unavailable");
        playUrl.Availability = new PlayUrlAvailability(
        [
            new PlayUrlVideoAvailability(
                112,
                13,
                PlayUrlStreamKind.Durl,
                "1080P+")
        ],
        []);

        var summary = VideoPagePlaybackMapper.BuildCapabilitySummary(playUrl, settings);

        Assert.Contains("configuredQuality=64", summary, StringComparison.Ordinal);
        Assert.Contains("requestedQuality=127", summary, StringComparison.Ordinal);
        Assert.Contains("supportQuality=[112(login=True,vip=True)]", summary, StringComparison.Ordinal);
        Assert.Contains("dashVideoIds=[64]", summary, StringComparison.Ordinal);
        Assert.Contains("availableVideo=[112:13:Durl]", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("media.invalid", summary, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', summary);
        Assert.DoesNotContain('\n', summary);
    }

    private ApplicationSettings CreateSettings(int videoQuality, bool isVip)
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new SettingsStore(Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"));
        return settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                Quality = videoQuality,
                VideoCodecs = 7
            },
            User = settingsStore.Current.User with
            {
                Mid = isVip ? 1 : -1,
                IsLogin = isVip,
                IsVip = isVip
            }
        };
    }

    private static PlayUrl CreatePlayUrl(params int[] qualities)
    {
        return new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = qualities
                    .Select(quality => new PlayUrlDashVideo
                    {
                        Id = quality,
                        CodecId = 7,
                        BaseAddress = $"https://media.invalid/video-{quality}"
                    })
                    .ToArray()
            },
            SupportFormats = qualities
                .Select(quality => new PlayUrlSupportFormat
                {
                    Quality = quality,
                    NewDescription = $"Quality {quality}"
                })
                .ToArray()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
