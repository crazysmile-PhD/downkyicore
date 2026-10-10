using DownKyi.Application.Desktop;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Presentation;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadContentConflictResolverTests
{
    [Fact]
    public async Task AvailableRequestPassesThroughWithoutDialog()
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with
        {
            Video = true,
            Subtitle = true
        };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreatePage(video: true, audio: false)));

        var page = Assert.Single(Assert.Single(finalized.Sections).Pages);
        Assert.Same(requested, page.RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task SidecarOnlyRequestWithoutPlaybackPassesThroughWithoutDialog(
        bool danmaku,
        bool subtitle,
        bool cover)
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with
        {
            Danmaku = danmaku,
            Subtitle = subtitle,
            Cover = cover
        };
        var page = CreatePage(video: false, audio: false);
        var prepared = CreatePreparedDownload(page);

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        Assert.False(page.HasPlayback);
        Assert.False(Assert.Single(Assert.Single(prepared.Sections).Pages).AvailableMedia.HasAnyMedia);
        Assert.Same(
            requested,
            Assert.Single(Assert.Single(finalized.Sections).Pages).RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task AvailableMediaChoiceBecomesPageRequestedContent()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));
        var requested = new DownloadContentSelection(
            Audio: true,
            Video: true,
            Danmaku: true,
            Subtitle: false,
            Cover: true);

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreatePage(video: true, audio: false)));

        var page = Assert.Single(Assert.Single(finalized.Sections).Pages);
        Assert.Equal(
            requested with { Audio = false, Video = true },
            page.RequestedContent);
        var prompt = Assert.IsType<DownloadContentConflictPrompt>(
            Assert.Single(dialogs.Requests).Parameters![DownloadContentConflictDialogContract.PromptParameter]);
        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
            prompt.Conflict.AvailableMedia);
        Assert.Equal(requested with { Audio = false, Video = true }, prompt.Conflict.AvailableContent);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DurlSupportedOutputModesPassThroughWithoutDialog(bool audio, bool video)
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with
        {
            Audio = audio,
            Video = video
        };
        var prepared = CreatePreparedDownload(CreateDurlPage());

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        var preparedPage = Assert.Single(Assert.Single(prepared.Sections).Pages);
        Assert.Equal(
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioVideo),
            preparedPage.AvailableMedia);
        Assert.True(preparedPage.AvailableMedia.Supports(requested));
        Assert.Same(
            requested,
            Assert.Single(Assert.Single(finalized.Sections).Pages).RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task AudioOnlyDurlHasNoCompatibleSubsetAndIsSkippedWithoutDialog()
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with { Audio = true };
        var prepared = CreatePreparedDownload(CreateDurlPage());
        var available = Assert.Single(Assert.Single(prepared.Sections).Pages).AvailableMedia;

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        Assert.False(available.Supports(requested));
        Assert.False(available.TryGetCompatibleContent(requested, out var compatible));
        Assert.False(compatible.Audio);
        Assert.False(compatible.Video);
        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task AudioOnlyDashWithoutVideoIsFinalized()
    {
        var dialogs = new RecordingDialogService();
        var page = CreatePage(video: false, audio: true);
        page.VideoQuality = null;
        var requested = DownloadContentSelection.None with { Audio = true };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(page));

        Assert.True(page.HasPlayback);
        var selected = Assert.Single(Assert.Single(finalized.Sections).Pages);
        Assert.Null(selected.VideoQuality);
        Assert.Same(requested, selected.RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task IndependentDashAudioRemainsAvailableWithDurlVideo()
    {
        var dialogs = new RecordingDialogService();
        var page = CreateDurlPage();
        page.AudioQualityFormat = "高质量";
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
        var requested = DownloadContentSelection.None with { Audio = true };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(page));

        Assert.Same(requested, Assert.Single(Assert.Single(finalized.Sections).Pages).RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task SkipChoiceRemovesPageBeforeTaskCreation()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: false));

        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: true, audio: false)));

        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task ApplyToAllReusesChoiceAcrossPreparedDownloads()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true));
        var resolver = new DownloadContentConflictResolver(dialogs);
        var choices = new DownloadContentConflictChoices();

        var first = await resolver.ResolveAsync(
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: true, audio: false)),
            isAll: false,
            choices,
            TestContext.Current.CancellationToken);
        var second = await resolver.ResolveAsync(
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: true, audio: false)),
            isAll: false,
            choices,
            TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(Assert.Single(first.Sections).Pages).RequestedContent.Audio);
        Assert.False(Assert.Single(Assert.Single(second.Sections).Pages).RequestedContent.Audio);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task LowerQualityUsesExistingConflictDialog()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));
        var requested = DownloadContentSelection.None with { Video = true };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreateLowerVideoQualityPage(64, "720P 高清")));

        var selected = Assert.Single(Assert.Single(finalized.Sections).Pages);
        Assert.Equal(64, selected.VideoQuality!.Quality);
        Assert.Same(requested, selected.RequestedContent);
        var prompt = Assert.IsType<DownloadContentConflictPrompt>(
            Assert.Single(dialogs.Requests).Parameters![DownloadContentConflictDialogContract.PromptParameter]);
        var substitution = Assert.IsType<DownloadQualitySubstitution>(
            prompt.Conflict.QualitySubstitutions.Video);
        Assert.Equal(80, substitution.RequestedQuality);
        Assert.Equal(64, substitution.SelectedQuality);
    }

    [Fact]
    public async Task ApplyToAllUsesEachPagesNearestLowerVideoQuality()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true));
        var requested = DownloadContentSelection.None with { Video = true };
        var prepared = CreatePreparedDownload(
            CreateLowerVideoQualityPage(64, "720P 高清"),
            CreateLowerVideoQualityPage(32, "480P 清晰", includeAudio: true),
            CreatePage(video: true, audio: false));

        Assert.NotEqual(
            prepared.Sections[0].Pages[0].AvailableMedia,
            prepared.Sections[0].Pages[1].AvailableMedia);

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        Assert.Equal(
            [64, 32, 80],
            Assert.Single(finalized.Sections).Pages
                .Select(page => page.VideoQuality!.Quality)
                .ToArray());
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task LowerQualitySkipDoesNotFinalizePage()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: false));

        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.None with { Video = true },
            CreatePreparedDownload(CreateLowerVideoQualityPage(64, "720P 高清")));

        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task ApplyToAllSkipRemainsPageScopedForQualitySubstitutions()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: true),
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));
        var requested = DownloadContentSelection.None with { Video = true };
        var prepared = CreatePreparedDownload(
            CreateLowerVideoQualityPage(64, "720P 高清"),
            CreateLowerVideoQualityPage(32, "480P 清晰"));

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        Assert.Equal(
            32,
            Assert.Single(Assert.Single(finalized.Sections).Pages).VideoQuality!.Quality);
        Assert.Equal(2, dialogs.Requests.Count);
    }

    [Fact]
    public async Task UnknownSelectedLowerQualityUsesNumericDisplayName()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));
        var requested = DownloadContentSelection.None with { Video = true };

        await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreateLowerVideoQualityPage(63, string.Empty)));

        var prompt = Assert.IsType<DownloadContentConflictPrompt>(
            Assert.Single(dialogs.Requests).Parameters![DownloadContentConflictDialogContract.PromptParameter]);
        Assert.Equal("63", prompt.Conflict.QualitySubstitutions.Video!.SelectedName);
    }

    [Fact]
    public async Task ApplyToAllAcceptsEachPagesOwnVideoOrAudioSubstitution()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true));
        var requested = DownloadContentSelection.None with { Audio = true, Video = true };
        var prepared = CreatePreparedDownload(
            CreateLowerVideoQualityPage(64, "720P 高清", includeAudio: true),
            CreateLowerAudioQualityPage());

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        Assert.Equal(2, Assert.Single(finalized.Sections).Pages.Count);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task UnrequestedLowerVideoQualityDoesNotAddOrPromptForVideo()
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with { Audio = true };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreateLowerVideoQualityPage(
                64,
                "720P 高清",
                includeAudio: true)));

        var selected = Assert.Single(Assert.Single(finalized.Sections).Pages);
        Assert.True(selected.RequestedContent.Audio);
        Assert.False(selected.RequestedContent.Video);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task ApplyToAllDoesNotHideADifferentConflict()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true),
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: false));
        var resolver = new DownloadContentConflictResolver(dialogs);
        var choices = new DownloadContentConflictChoices();

        await resolver.ResolveAsync(
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: true, audio: false)),
            isAll: false,
            choices,
            TestContext.Current.CancellationToken);
        var differentRequest = DownloadContentSelection.All with { Subtitle = false };
        var differentlyRequested = await resolver.ResolveAsync(
            differentRequest,
            CreatePreparedDownload(CreatePage(video: true, audio: false)),
            isAll: false,
            choices,
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(differentlyRequested.Sections).Pages);
        Assert.Equal(2, dialogs.Requests.Count);
    }

    [Fact]
    public async Task PageWithoutAnyMediaIsSkippedWithoutOfferingInvalidChoice()
    {
        var dialogs = new RecordingDialogService();

        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: false, audio: false)));

        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task AddresslessDurlDoesNotBecomeAnAvailableOutputMode()
    {
        var dialogs = new RecordingDialogService();
        var page = CreateDurlPage(hasUsableAddress: false);
        var prepared = CreatePreparedDownload(page);
        var preparedPage = Assert.Single(Assert.Single(prepared.Sections).Pages);

        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.None with { Video = true },
            prepared);

        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.None),
            preparedPage.AvailableMedia);
        Assert.False(page.HasPlayback);
        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Empty(dialogs.Requests);
    }

    private static Task<FinalizedDownload> ResolveAsync(
        RecordingDialogService dialogs,
        DownloadContentSelection requested,
        PreparedDownload prepared) => new DownloadContentConflictResolver(dialogs).ResolveAsync(
            requested,
            prepared,
            isAll: false,
            new DownloadContentConflictChoices(),
            TestContext.Current.CancellationToken);

    private static PreparedDownload CreatePreparedDownload(params VideoPage[] pages) => PreparedDownload.Create(
        new VideoInfoView(),
        [
            new VideoSection
            {
                VideoPages = pages
            }
        ]);

    private static VideoPage CreateLowerVideoQualityPage(
        int quality,
        string qualityName,
        bool includeAudio = false)
    {
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = quality,
                        CodecId = 7,
                        BaseAddress = $"https://media.invalid/video-{quality}.m4s"
                    }
                ],
                Audio = includeAudio
                    ?
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30280,
                            BaseAddress = "https://media.invalid/audio-30280.m4s"
                        }
                    ]
                    : []
            }
        };
        var selected = new VideoQuality
        {
            Quality = quality,
            QualityFormat = qualityName,
            SelectedVideoCodec = "H.264/AVC"
        };
        var page = new VideoPage
        {
            IsSelected = true,
            Name = $"page-{quality}",
            AudioQualityFormat = includeAudio ? "高质量" : string.Empty,
            PlaybackAvailability = PlayUrlAvailability.From(playUrl)
        };
        page.SetAutomaticVideoQuality(
            selected,
            new PlaybackQualityMatch(
                RequestedQuality: 80,
                SelectedQuality: quality,
                PlaybackQualityMatchKind.Lower));
        return page;
    }

    private static VideoPage CreatePage(bool video, bool audio)
    {
        var playUrl = new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = video
                    ?
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 80,
                            CodecId = 7,
                            BaseAddress = "https://media.invalid/video.m4s"
                        }
                    ]
                    : [],
                Audio = audio
                    ?
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30280,
                            BaseAddress = "https://media.invalid/audio.m4s"
                        }
                    ]
                    : []
            }
        };
        return new VideoPage
        {
            IsSelected = true,
            Name = "page",
            AudioQualityFormat = audio ? "高质量" : string.Empty,
            PlaybackAvailability = PlayUrlAvailability.From(playUrl),
            VideoQuality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P",
                SelectedVideoCodec = "H.264/AVC"
            }
        };
    }

    private static VideoPage CreateLowerAudioQualityPage()
    {
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
                        BaseAddress = "https://media.invalid/video-80.m4s"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30232,
                        BaseAddress = "https://media.invalid/audio-30232.m4s"
                    }
                ]
            }
        };
        var page = new VideoPage
        {
            IsSelected = true,
            Name = "page-audio-30232",
            PlaybackAvailability = PlayUrlAvailability.From(playUrl),
            VideoQuality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P 高清",
                SelectedVideoCodec = "H.264/AVC"
            }
        };
        page.SetAutomaticAudioQuality(
            "中质量",
            new PlaybackQualityMatch(
                RequestedQuality: 30280,
                SelectedQuality: 30232,
                PlaybackQualityMatchKind.Lower));
        return page;
    }

    private static VideoPage CreateDurlPage(bool hasUsableAddress = true)
    {
        var playUrl = new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = hasUsableAddress
                        ? "https://media.invalid/combined.mp4"
                        : string.Empty
                }
            ]
        };
        return new VideoPage
        {
            IsSelected = true,
            Name = "page",
            PlaybackAvailability = PlayUrlAvailability.From(playUrl),
            VideoQuality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P",
                IsDurl = true,
                SelectedVideoCodec = "H.264/AVC"
            }
        };
    }

    private sealed class RecordingDialogService(params DownloadContentConflictDecision[] decisions)
        : IAppDialogService
    {
        private readonly Queue<DownloadContentConflictDecision> _decisions = new(decisions);

        public List<AppDialogRequest> Requests { get; } = [];

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var decision = _decisions.Dequeue();
            return Task.FromResult(new AppDialogResult(
                AppDialogOutcome.Accepted,
                DownloadContentConflictDialogContract.Encode(decision)));
        }
    }
}
