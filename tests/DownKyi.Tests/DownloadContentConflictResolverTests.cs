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
