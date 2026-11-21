using DownKyi.Application.Downloads;
using DownKyi.Application.FileName;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Models;
using DownKyi.Presentation;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.Tests;

public sealed class DownloadAddOwnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-download-add-owner-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DraftFactoryPreservesTaskIdentityQualityContentAndStreamType()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";
        page.EpisodeId = 99;
        page.Page = 2;
        page.Duration = "01:23";
        page.FirstFrame = "frame";
        var video = new VideoInfoView
        {
            CoverUrl = "cover",
            Title = "main",
            TypeId = -10,
            VideoZone = "Technology>Software"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };
        var content = new DownloadContentSelection(
            Audio: true,
            Video: false,
            Danmaku: true,
            Subtitle: false,
            Cover: true);

        var item = CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 2,
            page,
            CreateVideoQuality(),
            settingsStore.Current,
            content);

        Assert.Equal(page.Bvid, item.DownloadBase.Bvid);
        Assert.Equal(page.Avid, item.DownloadBase.Avid);
        Assert.Equal(page.Cid, item.DownloadBase.Cid);
        Assert.Equal(page.EpisodeId, item.DownloadBase.EpisodeId);
        Assert.Equal(page.Page, item.DownloadBase.Page);
        Assert.Equal(0, item.Resolution.Id);
        Assert.Equal(string.Empty, item.Resolution.Name);
        Assert.Equal(string.Empty, item.VideoCodecName);
        Assert.Equal(
            DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Cheese,
            item.Downloading.PlayStreamType);
        Assert.Equal(DownKyi.Models.DownloadStatus.NotStarted, item.Downloading.DownloadStatus);
        Assert.Equal(
            content with
            {
                MediaKind = DownloadMediaKind.Dash,
                DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
            },
            item.DownloadBase.NeedDownloadContent);
        Assert.StartsWith(_directory, item.DownloadBase.FilePath, StringComparison.Ordinal);
        Assert.Contains("section", item.DownloadBase.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftFactoryDefersCollisionResolutionToAtomicAdmission()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current;
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";
        var video = new VideoInfoView
        {
            Title = "main",
            VideoZone = "Technology"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };
        var first = CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(),
            settings,
            DownloadContentSelection.All);
        Directory.CreateDirectory(Path.GetDirectoryName(first.DownloadBase.FilePath)!);
        File.WriteAllText($"{first.DownloadBase.FilePath}.mp4", "occupied");

        var second = CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(),
            settings,
            DownloadContentSelection.All);

        Assert.Equal(first.DownloadBase.FilePath, second.DownloadBase.FilePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FlatFileNameTemplateRespectsTitleDirectoryPreference(bool useTitleDirectory)
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                FileNameParts = [FileNamePart.PageTitle],
                UseTitlePackageDirectory = useTitleDirectory
            }
        };
        var page = CreatePage();
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { VideoPages = [page] };

        var draft = CreateDraft(_directory, video, section, 1, page,
            CreateVideoQuality(), settings, DownloadContentSelection.None with { Video = true });

        var expectedDirectory = useTitleDirectory ? Path.Combine(_directory, "main") : _directory;
        Assert.Equal(Path.Combine(expectedDirectory, "page"), draft.DownloadBase.FilePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreatorAndTitleTemplateRespectsTitleDirectoryPreference(bool useTitleDirectory)
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Video = settingsStore.Current.Video with
            {
                UseTitlePackageDirectory = useTitleDirectory,
                FileNameParts =
                [
                    FileNamePart.UpMid, FileNamePart.Plus,
                    FileNamePart.UpName, FileNamePart.Plus,
                    FileNamePart.VideoPublishTime, FileNamePart.Plus,
                    FileNamePart.MainTitle, FileNamePart.Plus,
                    FileNamePart.Order, FileNamePart.Hyphen,
                    FileNamePart.Bvid, FileNamePart.Slash,
                    FileNamePart.Section, FileNamePart.Plus,
                    FileNamePart.Bvid, FileNamePart.Plus,
                    FileNamePart.PageTitle, FileNamePart.Hyphen,
                    FileNamePart.VideoQuality, FileNamePart.Hyphen,
                    FileNamePart.VideoCodec
                ]
            }
        };
        var page = CreatePage();
        page.Owner = new() { Mid = 42, Name = "creator" };
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { Title = "section", VideoPages = [page] };

        var draft = CreateDraft(_directory, video, section, 2, page,
            CreateVideoQuality(), settings, DownloadContentSelection.None with { Video = true });

        var expectedDirectory = useTitleDirectory ? Path.Combine(_directory, "main") : _directory;
        Assert.Equal(
            Path.Combine(expectedDirectory, "42+creator+2024-01-02+main+1-BV1test", "section+BV1test+page-1080P-AVC"),
            Path.GetFullPath(draft.DownloadBase.FilePath));
    }

    [Fact]
    public void DefaultTemplateUsesOneTitleDirectoryAboveSections()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { Title = "section", VideoPages = [page] };

        var draft = CreateDraft(_directory, video, section, 2, page,
            CreateVideoQuality(), settingsStore.Current,
            DownloadContentSelection.None with { Video = true });

        Assert.Equal(Path.Combine(_directory, "main", "section", "1-page-1080P-AVC"),
            Path.GetFullPath(draft.DownloadBase.FilePath));
    }

    [Fact]
    public void DraftFactoryRejectsAudioOnlyDurlContract()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.PlaybackAvailability = PlayUrlAvailability.From(CreateDurlPlayUrl());
        var video = new VideoInfoView
        {
            Title = "main",
            VideoZone = "Technology"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };

        var error = Assert.Throws<InvalidOperationException>(() => CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(isDurl: true),
            settingsStore.Current,
            DownloadContentSelection.None with { Audio = true }));

        Assert.Equal(
            "A media download draft requires a supported finalized playback selection.",
            error.Message);
    }

    [Fact]
    public void DraftFactoryUsesIndependentDashAudioWithoutSelectedVideo()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";
        page.VideoQuality = null;
        page.PlaybackAvailability = PlayUrlAvailability.From(new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl = [new PlayUrlDurl { SourceAddress = "https://media.invalid/video.mp4" }],
            Dash = new PlayUrlDash
            {
                Audio = [new PlayUrlDashVideo
                {
                    Id = 30280,
                    BaseAddress = "https://media.invalid/audio.m4s"
                }]
            }
        });
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { VideoPages = [page] };

        var item = CreateDraft(
            _directory,
            video,
            section,
            1,
            page,
            null,
            settingsStore.Current,
            DownloadContentSelection.None with { Audio = true });

        Assert.Equal(DownloadMediaKind.Dash, item.DownloadBase.NeedDownloadContent.MediaKind);
        Assert.Equal(0, item.Resolution.Id);
        Assert.Equal(string.Empty, item.VideoCodecName);
        Assert.Equal(30280, item.AudioCodec.Id);
    }

    [Fact]
    public void DraftFactoryUsesFinalizedPlaybackSnapshotAfterPageChanges()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";
        var videoQuality = Assert.IsType<VideoQuality>(page.VideoQuality);
        var audioQualityFormat = page.AudioQualityFormat;
        var availableMedia = DownloadMediaCapabilities.From(
            page.PlaybackAvailability,
            videoQuality,
            audioQualityFormat);
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { VideoPages = [page] };

        page.VideoQuality = null;
        page.AudioQualityFormat = string.Empty;
        page.PlaybackAvailability = null;

        var item = DownloadTaskDraftFactory.Create(
            _directory,
            video,
            section,
            1,
            page,
            videoQuality,
            audioQualityFormat,
            availableMedia,
            settingsStore.Current,
            DownloadContentSelection.None with { Audio = true, Video = true });

        Assert.Equal(DownloadMediaKind.Dash, item.DownloadBase.NeedDownloadContent.MediaKind);
        Assert.Equal(80, item.Resolution.Id);
        Assert.Equal("H.264/AVC", item.VideoCodecName);
        Assert.Equal(30280, item.AudioCodec.Id);
    }

    [Fact]
    public void DraftFactoryPersistsFinalizedSelectedMediaKind()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.PlaybackAvailability = PlayUrlAvailability.From(CreateDurlPlayUrl());
        var video = new VideoInfoView
        {
            Title = "main",
            TypeId = 13,
            VideoZone = "Anime"
        };
        var section = new VideoSection
        {
            Title = "section",
            VideoPages = [page]
        };

        var item = CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            CreateVideoQuality(isDurl: true),
            settingsStore.Current,
            DownloadContentSelection.None with { Video = true });

        Assert.Equal(DownloadMediaKind.Durl, item.DownloadBase.NeedDownloadContent.MediaKind);
        Assert.Equal(0, item.AudioCodec.Id);
        Assert.Contains(
            page.PlaybackAvailability!.Video,
            video => video.StreamKind == PlayUrlStreamKind.Durl);
    }

    [Fact]
    public void DraftFactoryUsesTheSelectedCandidateWhenQualityIdsMatchAcrossStreamKinds()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Infrastructure.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var page = CreatePage();
        page.PlaybackAvailability = new PlayUrlAvailability(
        [
            new PlayUrlVideoAvailability(80, 12, PlayUrlStreamKind.Dash, "1080P DASH"),
            new PlayUrlVideoAvailability(80, 7, PlayUrlStreamKind.Durl, "1080P DURL")
        ],
        []);
        var durl = CreateVideoQuality(isDurl: true);
        durl.Quality = 80;
        durl.SelectedVideoCodec = "H.264/AVC";
        page.VideoQuality = durl;
        var video = new VideoInfoView { Title = "main", VideoZone = "Technology" };
        var section = new VideoSection { VideoPages = [page] };

        var item = CreateDraft(
            _directory,
            video,
            section,
            sectionCount: 1,
            page,
            page.VideoQuality,
            settingsStore.Current,
            DownloadContentSelection.None with { Video = true });

        Assert.Equal(DownloadMediaKind.Durl, item.DownloadBase.NeedDownloadContent.MediaKind);
        Assert.Equal("H.264/AVC", item.VideoCodecName);
        Assert.Equal(80, item.Resolution.Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static DownloadingItem CreateDraft(
        string directory,
        VideoInfoView video,
        VideoSection section,
        int sectionCount,
        VideoPage page,
        VideoQuality? videoQuality,
        DownKyi.Application.Settings.ApplicationSettings settings,
        DownloadContentSelection content)
    {
        var audioQualityFormat = page.AudioQualityFormat;
        return DownloadTaskDraftFactory.Create(
            directory,
            video,
            section,
            sectionCount,
            page,
            videoQuality,
            audioQualityFormat,
            DownloadMediaCapabilities.From(
                page.PlaybackAvailability,
                videoQuality,
                audioQualityFormat),
            settings,
            content);
    }

    private static VideoPage CreatePage()
    {
        return new VideoPage
        {
            Avid = 42,
            Bvid = "BV1test",
            Cid = 84,
            IsSelected = true,
            Name = "page",
            Order = 1,
            OriginalPublishTime = new DateTime(2024, 1, 2),
            PublishTime = "2024-01-02",
            PlaybackAvailability = PlayUrlAvailability.From(CreateDashPlayUrl()),
            VideoQuality = CreateVideoQuality()
        };
    }

    private static PlayUrl CreateDashPlayUrl()
    {
        return new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video-80"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        BaseAddress = "https://media.invalid/audio-30280"
                    }
                ]
            }
        };
    }

    private static PlayUrl CreateDurlPlayUrl()
    {
        return new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://media.invalid/segment-1"
                }
            ]
        };
    }

    private static VideoQuality CreateVideoQuality(bool isDurl = false)
    {
        return new VideoQuality
        {
            Quality = 80,
            QualityFormat = "1080P",
            IsDurl = isDurl,
            SelectedVideoCodec = "H.264/AVC"
        };
    }

}
