using DownKyi.Application.Desktop;
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

    [Fact]
    public async Task BatchOffersAudioOnlyWhenVideoCannotBeSelected()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));
        var unselected = CreatePage(video: true, audio: true);
        unselected.PlaybackAvailability = PlayUrlAvailability.From(new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 112,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video-112.m4s"
                    }
                ],
                Audio =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 30280,
                        BaseAddress = "https://media.invalid/audio.m4s"
                    }
                ]
            }
        });
        unselected.VideoQuality = null;
        unselected.VideoQualityList =
        [
            new VideoQuality
            {
                Quality = 112,
                QualityFormat = "1080P+",
                SelectedVideoCodec = "H.264/AVC"
            }
        ];
        var selected = CreatePage(video: true, audio: true);
        selected.Name = "selected";
        var prepared = PreparedDownload.Create(
            new VideoInfoView(),
            [new VideoSection { VideoPages = [unselected, selected] }]);
        var requested = DownloadContentSelection.None with { Video = true, Audio = true };

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        var pages = Assert.Single(finalized.Sections).Pages;
        Assert.Equal(2, pages.Count);
        var audioAlternative = Assert.Single(pages, item => item.Page == unselected);
        Assert.True(audioAlternative.RequestedContent.Audio);
        Assert.False(audioAlternative.RequestedContent.Video);
        Assert.Null(audioAlternative.VideoQuality);
        Assert.Same(selected, Assert.Single(pages, item => item.Page == selected).Page);
        var audioOnly = await ResolveAsync(
            dialogs,
            DownloadContentSelection.None with { Audio = true },
            CreatePreparedDownload(unselected));
        Assert.Same(unselected, Assert.Single(Assert.Single(audioOnly.Sections).Pages).Page);
        Assert.Single(dialogs.Requests);
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
    public async Task PageWithoutMediaOffersOnlyOriginallySelectedSidecars()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: false));

        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.All,
            CreatePreparedDownload(CreatePage(video: false, audio: false)));

        var content = Assert.Single(Assert.Single(finalized.Sections).Pages).RequestedContent;
        Assert.False(content.Video);
        Assert.False(content.Audio);
        Assert.True(content.Danmaku && content.Subtitle && content.Cover);
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task OnlySelectedSidecarsPassWithoutPlaybackOrPrompt()
    {
        var dialogs = new RecordingDialogService();
        var requested = DownloadContentSelection.None with
        {
            Danmaku = true,
            Cover = true
        };

        var finalized = await ResolveAsync(
            dialogs,
            requested,
            CreatePreparedDownload(CreatePage(video: false, audio: false)));

        Assert.Equal(requested, Assert.Single(Assert.Single(finalized.Sections).Pages).RequestedContent);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task VideoOnlyRequestDoesNotBecomeAudioOnly()
    {
        var dialogs = new RecordingDialogService();
        var finalized = await ResolveAsync(
            dialogs,
            DownloadContentSelection.None with { Video = true },
            CreatePreparedDownload(CreatePage(video: false, audio: true)));

        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        Assert.Empty(dialogs.Requests);
    }

    [Fact]
    public async Task ApplyToAllUsesEachPagesNearestLowerQuality()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true));
        var pages = new[]
        {
            CreatePageAtQuality(64),
            CreatePageAtQuality(32),
            CreatePageAtQuality(80),
            CreatePageAtQuality(64)
        };
        var prepared = PreparedDownload.Create(
            new VideoInfoView(),
            [new VideoSection { VideoPages = pages }],
            preferredVideoQuality: 80,
            preferredAudioQuality: 30280);

        var finalized = await ResolveAsync(dialogs, DownloadContentSelection.All, prepared);

        Assert.Equal([64, 32, 80, 64],
            Assert.Single(finalized.Sections).Pages.Select(page => page.VideoQuality!.Quality));
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task ApplyToAllUsesEachPagesAvailableMediaWithoutAddingUnselectedContent()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableMedia,
                ApplyToAll: true));
        var requested = DownloadContentSelection.None with
        {
            Video = true,
            Audio = true,
            Cover = true
        };
        var prepared = PreparedDownload.Create(
            new VideoInfoView(),
            [new VideoSection
            {
                VideoPages =
                [
                    CreatePage(video: true, audio: false),
                    CreatePage(video: false, audio: true)
                ]
            }]);

        var finalized = await ResolveAsync(dialogs, requested, prepared);

        var pages = Assert.Single(finalized.Sections).Pages;
        Assert.Equal(2, pages.Count);
        Assert.True(pages[0].RequestedContent.Video);
        Assert.False(pages[0].RequestedContent.Audio);
        Assert.False(pages[1].RequestedContent.Video);
        Assert.True(pages[1].RequestedContent.Audio);
        Assert.All(pages, page => Assert.True(page.RequestedContent.Cover));
        Assert.Single(dialogs.Requests);
    }

    [Fact]
    public async Task LowerAudioQualityUsesExistingChoiceFlow()
    {
        var dialogs = new RecordingDialogService(
            new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: false));
        var page = CreatePage(video: true, audio: true);
        page.PlaybackAvailability = PlayUrlAvailability.From(new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo
                {
                    Id = 80, CodecId = 7, BaseAddress = "https://media.invalid/video"
                }],
                Audio = [new PlayUrlDashVideo
                {
                    Id = 30232, BaseAddress = "https://media.invalid/audio"
                }]
            }
        });
        page.AudioQualityFormat = "中质量";
        var prepared = PreparedDownload.Create(
            new VideoInfoView(),
            [new VideoSection { VideoPages = [page] }],
            preferredVideoQuality: 80,
            preferredAudioQuality: 30280);

        var finalized = await ResolveAsync(dialogs, DownloadContentSelection.All, prepared);

        Assert.Empty(Assert.Single(finalized.Sections).Pages);
        var prompt = Assert.IsType<DownloadContentConflictPrompt>(
            Assert.Single(dialogs.Requests).Parameters![DownloadContentConflictDialogContract.PromptParameter]);
        Assert.Equal("中质量", prompt.Conflict.AvailableMedia.LowerAudioQuality);
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

    private static PreparedDownload CreatePreparedDownload(VideoPage page) => PreparedDownload.Create(
        new VideoInfoView(),
        [
            new VideoSection
            {
                VideoPages = [page]
            }
        ]);

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

    private static VideoPage CreatePageAtQuality(int quality)
    {
        var page = CreatePage(video: true, audio: true);
        page.PlaybackAvailability = PlayUrlAvailability.From(new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo
                {
                    Id = quality,
                    CodecId = 7,
                    BaseAddress = $"https://media.invalid/video-{quality}"
                }],
                Audio = [new PlayUrlDashVideo
                {
                    Id = 30280,
                    BaseAddress = "https://media.invalid/audio"
                }]
            }
        });
        page.VideoQuality = new VideoQuality
        {
            Quality = quality,
            QualityFormat = $"Quality {quality}",
            SelectedVideoCodec = "H.264/AVC"
        };
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
