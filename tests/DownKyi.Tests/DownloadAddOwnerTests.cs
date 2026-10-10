using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
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
    public async Task ActiveDuplicateIsSkippedWithoutOwningUiNotification()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        context.ListState.AddDownloading(CreateDownloadingItem());

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.ReDownload,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Empty(context.Notifications.Messages);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task VideoOnlyActiveTaskDoesNotBlockAudioOnlyRequest()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var videoOnly = CreateDownloadingItem();
        videoOnly.DownloadBase.NeedDownloadContent = DownloadContentSelection.None with
        {
            Video = true,
            MediaKind = DownloadMediaKind.Dash
        };
        context.ListState.AddDownloading(videoOnly);
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(
                DownloadContentSelection.None with { Audio = true },
                page,
                videoQuality: null),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
        Assert.Empty(context.Notifications.Messages);
    }

    [Fact]
    public async Task ExistingMediaOnlyTaskDoesNotBlockAddingSubtitleToTheSameOutput()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var mediaOnly = CreateRequestedItem(DownloadContentSelection.None with
        {
            Video = true,
            MediaKind = DownloadMediaKind.Dash
        });
        context.ListState.AddDownloading(mediaOnly);
        var mediaWithSubtitle = mediaOnly.DownloadBase.NeedDownloadContent with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        var resolution = await context.Policy.ResolveAsync(
            CreateRequestedItem(mediaWithSubtitle),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.False(resolution.IsFullyCovered);
        Assert.True(resolution.AllowExistingBasePath);
        Assert.Equal(
            mediaWithSubtitle with
            {
                Audio = false,
                Video = false,
                MediaKind = DownloadMediaKind.None
            },
            resolution.RemainingContent);
    }

    [Fact]
    public async Task DisjointSubtitleRequestStillRecognizesTheMatchingOutputOwner()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var mediaOnly = CreateRequestedItem(DownloadContentSelection.None with
        {
            Video = true,
            MediaKind = DownloadMediaKind.Dash
        });
        context.ListState.AddDownloading(mediaOnly);
        var subtitleOnly = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        var resolution = await context.Policy.ResolveAsync(
            CreateRequestedItem(subtitleOnly),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.Equal(subtitleOnly, resolution.RemainingContent);
        Assert.True(resolution.AllowExistingBasePath);
    }

    [Fact]
    public async Task CompletedMediaOwnerAllowsSubtitleOnlyReDownloadAtTheSameBasePath()
    {
        var mediaOnly = DownloadContentSelection.None with
        {
            Video = true,
            MediaKind = DownloadMediaKind.Dash
        };
        using var context = DuplicatePolicyContext.WithCompleted(
            AppDialogOutcome.Canceled,
            content: mediaOnly);
        var subtitleOnly = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        var resolution = await context.Policy.ResolveAsync(
            CreateRequestedItem(subtitleOnly),
            DownKyi.Core.Settings.RepeatDownloadStrategy.ReDownload,
            TestContext.Current.CancellationToken);

        Assert.Equal(subtitleOnly, resolution.RemainingContent);
        Assert.True(resolution.AllowExistingBasePath);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task ExistingMediaAndSubtitleTaskBlocksTheSameSubtitleOnlyOutput()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var mediaAndSubtitle = DownloadContentSelection.None with
        {
            Video = true,
            Subtitle = true,
            MediaKind = DownloadMediaKind.Dash,
            SelectedSubtitleTrackIds = [11]
        };
        context.ListState.AddDownloading(CreateRequestedItem(mediaAndSubtitle));
        var subtitleOnly = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(subtitleOnly),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
    }

    [Fact]
    public async Task SubtitleDuplicateRequiresTheSameTrackSelection()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var existing = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11, 22],
            DefaultSubtitleTrackId = 11
        };
        context.ListState.AddDownloading(CreateRequestedItem(existing));

        Assert.True(await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(existing with { SelectedSubtitleTrackIds = [22, 11] }),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));
        Assert.False(await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(existing with
            {
                SelectedSubtitleTrackIds = [22],
                DefaultSubtitleTrackId = 22
            }),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompletedSubtitleDuplicateRequiresAnExistingPublishedTrackOutput()
    {
        var subtitle = DownloadContentSelection.None with
        {
            Subtitle = true,
            SelectedSubtitleTrackIds = [11]
        };
        using var context = DuplicatePolicyContext.WithCompleted(
            AppDialogOutcome.Canceled,
            content: subtitle);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(subtitle),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
    }

    [Fact]
    public async Task DanmakuDuplicateRequiresTheSameOutputFormat()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var ass = DownloadContentSelection.None with
        {
            Danmaku = true,
            DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
        };
        context.ListState.AddDownloading(CreateRequestedItem(ass));

        Assert.True(await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(ass),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));
        Assert.False(await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(ass with { DanmakuOutputFormat = DownloadDanmakuOutputFormat.Xml }),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CoverOnlyTaskIsDetectedAsDuplicateByActionAndOutputPath()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Canceled);
        var cover = DownloadContentSelection.None with { Cover = true };
        context.ListState.AddDownloading(CreateRequestedItem(cover));

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(cover),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
    }

    [Fact]
    public async Task CompletedVideoOnlyTaskDoesNotBlockAudioOnlyRequest()
    {
        using var context = DuplicatePolicyContext.WithCompleted(
            AppDialogOutcome.Canceled,
            content: DownloadContentSelection.None with
            {
                Video = true,
                MediaKind = DownloadMediaKind.Dash
            });
        var page = CreatePage();
        page.AudioQualityFormat = "高质量";

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(
                DownloadContentSelection.None with { Audio = true },
                page,
                videoQuality: null),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
    }

    [Fact]
    public async Task CompletedVideoOnlyTaskStillBlocksSameVideoOutput()
    {
        var videoOnly = DownloadContentSelection.None with
        {
            Video = true,
            MediaKind = DownloadMediaKind.Dash
        };
        using var context = DuplicatePolicyContext.WithCompleted(
            AppDialogOutcome.Canceled,
            content: videoOnly);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(videoOnly),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Equal(videoOnly, context.Store.History!.RequestedContent);
    }

    [Fact]
    public async Task ActiveDuplicateIsSkippedBeforeCompletedHistoryIsRead()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);
        context.ListState.AddDownloading(CreateDownloadingItem());
        var completedCandidates = new Lazy<Task<List<DownloadedItem>>>(() =>
            context.Policy.LoadCompletedCandidatesAsync(
                TestContext.Current.CancellationToken));

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken,
            completedCandidates);

        Assert.True(shouldSkip);
        Assert.False(completedCandidates.IsValueCreated);
        Assert.Equal(0, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task CompletedDuplicateJumpOverPreservesHistory()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.NotNull(context.Store.History);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task CompletedDuplicateReDownloadAllowsNewTaskWithoutDeletingHistory()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Canceled);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.ReDownload,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.NotNull(context.Store.History);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task RejectedDuplicateConfirmationPreservesCompletedRecord()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Canceled);

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
            TestContext.Current.CancellationToken);

        Assert.True(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.NotNull(context.Store.History);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(1, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task AcceptedDuplicateConfirmationDeletesPersistedRecordBeforeAllowingTask()
    {
        using var context = DuplicatePolicyContext.WithCompleted(
            AppDialogOutcome.Accepted,
            loadUi: true);
        Assert.NotNull(context.Store.History);
        var historyId = context.Store.History.Id;

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
            TestContext.Current.CancellationToken);

        Assert.False(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.Null(context.Store.Current);
        Assert.Null(context.Store.History);
        Assert.Equal(0, context.Store.UpdateCount);
        Assert.Equal(1, context.Store.DeleteHistoryCount);
        Assert.Equal(historyId, context.Store.DeletedHistoryId);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
        Assert.Equal(1, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task AcceptedDuplicateConfirmationSuppressesAStalePendingHistorySnapshot()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);
        var staleSnapshot = DownloadTaskProjectionMapper.ToDownloadedItem(context.Store.History!);
        var completedCandidates = new Lazy<Task<List<DownloadedItem>>>(() =>
            context.Policy.LoadCompletedCandidatesAsync(
                TestContext.Current.CancellationToken));

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
            TestContext.Current.CancellationToken,
            completedCandidates);
        context.ListState.LoadDownloadedHistory([staleSnapshot]);

        Assert.False(shouldSkip);
        Assert.Empty(context.ListState.Downloaded);
        Assert.True(context.ListState.IsDownloadedHistoryLoaded);
    }

    [Fact]
    public async Task CachedCandidatesIncludeACompletionProjectedDuringTheBatch()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Accepted);
        var candidateList = await context.Policy.LoadCompletedCandidatesAsync(
            TestContext.Current.CancellationToken);
        var completedCandidates = new Lazy<Task<List<DownloadedItem>>>(() =>
            Task.FromResult(candidateList));
        context.ListState.AddDownloaded(DownloadTaskProjectionMapper.ToDownloadedItem(
            DuplicatePolicyContext.CreateCompletedHistory()));

        var shouldSkip = await context.Policy.ShouldSkipAsync(
            CreateRequestedItem(DownloadContentSelection.All),
            DownKyi.Core.Settings.RepeatDownloadStrategy.JumpOver,
            TestContext.Current.CancellationToken,
            completedCandidates);

        Assert.True(shouldSkip);
        Assert.Single(candidateList);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task DuplicatePolicyPropagatesCancellationBeforeInspectingLists()
    {
        using var context = new DuplicatePolicyContext(AppDialogOutcome.Accepted);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Policy.ShouldSkipAsync(
                CreateRequestedItem(DownloadContentSelection.All),
                DownKyi.Core.Settings.RepeatDownloadStrategy.Ask,
                cancellation.Token));
        Assert.Equal(0, context.Dialogs.ShowCount);
    }

    [Fact]
    public async Task DurableHistoryReadDoesNotCaptureTheCallingSynchronizationContext()
    {
        using var context = DuplicatePolicyContext.WithCompleted(AppDialogOutcome.Accepted);
        var historyPage = new TaskCompletionSource<DownloadHistoryPage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Store.PendingHistoryPage = historyPage.Task;
        var originalContext = SynchronizationContext.Current;
        var blockedContext = new NonPumpingSynchronizationContext();
        Task<IReadOnlyList<DownloadedItem>> read;
        try
        {
            SynchronizationContext.SetSynchronizationContext(blockedContext);
            read = context.ProjectionStore.GetDownloadedAsync(
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        historyPage.SetResult(new DownloadHistoryPage([context.Store.History!], null));
        var items = await read.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.Single(items);
        Assert.Equal(0, blockedContext.PostCount);
    }

    [Fact]
    public void DraftFactoryPreservesTaskIdentityQualityContentAndStreamType()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
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

        var item = DownloadTaskDraftFactory.Create(
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
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        var settings = settingsStore.Current with
        {
            Basic = settingsStore.Current.Basic with
            {
                RepeatFileAutoAddNumberSuffix = true
            }
        };
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
        var first = DownloadTaskDraftFactory.Create(
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

        var second = DownloadTaskDraftFactory.Create(
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

    [Fact]
    public void DraftFactoryRejectsAudioOnlyDurlContract()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
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

        var error = Assert.Throws<InvalidOperationException>(() => DownloadTaskDraftFactory.Create(
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
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
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

        var item = DownloadTaskDraftFactory.Create(
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
    public void DraftFactoryPersistsFinalizedSelectedMediaKind()
    {
        Directory.CreateDirectory(_directory);
        using var settingsStore = new DownKyi.Core.Settings.SettingsStore(
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

        var item = DownloadTaskDraftFactory.Create(
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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
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

    private static DownloadingItem CreateDownloadingItem()
    {
        return new DownloadingItem
        {
            DownloadBase = new DownloadBase
            {
                Id = "duplicate-task",
                Avid = 42,
                Bvid = "BV1test",
                Cid = 84,
                FilePath = Path.Combine(
                    Path.GetDirectoryName(typeof(DownloadAddOwnerTests).Assembly.Location)!,
                    Path.GetFileNameWithoutExtension(typeof(DownloadAddOwnerTests).Assembly.Location)),
                Name = "page",
                Resolution = new DownKyi.Core.BiliApi.BiliUtils.Quality
                {
                    Id = 80,
                    Name = "1080P"
                },
                VideoCodecName = "H.264/AVC"
            },
            Downloading = new Downloading
            {
                DownloadStatus = DownKyi.Models.DownloadStatus.NotStarted,
                PlayStreamType = DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video
            }
        };
    }

    private static DownloadingItem CreateRequestedItem(
        DownloadContentSelection content,
        VideoPage? page = null,
        VideoQuality? videoQuality = null)
    {
        page ??= CreatePage();
        videoQuality ??= page.VideoQuality;
        var item = CreateDownloadingItem();
        item.DownloadBase.Cid = page.Cid;
        item.DownloadBase.NeedDownloadContent = content;
        item.DownloadBase.Resolution = content.Video && videoQuality != null
            ? new DownKyi.Core.BiliApi.BiliUtils.Quality
            {
                Id = videoQuality.Quality,
                Name = videoQuality.QualityFormat
            }
            : new DownKyi.Core.BiliApi.BiliUtils.Quality();
        item.DownloadBase.VideoCodecName = content.Video
            ? videoQuality?.SelectedVideoCodec ?? string.Empty
            : string.Empty;
        item.DownloadBase.AudioCodec = content.Audio
            ? new DownKyi.Core.BiliApi.BiliUtils.Quality
            {
                Name = page.AudioQualityFormat
            }
            : new DownKyi.Core.BiliApi.BiliUtils.Quality();
        return item;
    }

    private sealed class DuplicatePolicyContext : IDisposable
    {
        private readonly DownloadTaskApplicationService _taskService;
        private readonly DownloadTaskProjectionStore _projectionStore;

        public DuplicatePolicyContext(
            AppDialogOutcome outcome,
            DownloadTask? current = null,
            DownloadHistoryRecord? history = null)
        {
            Store = new MutableDownloadTaskStore(current, history);
            var historyService = DownloadHistoryService.CreateForSharedStore(Store);
            _taskService = new DownloadTaskApplicationService(Store, historyService, new SystemClock());
            _projectionStore = new DownloadTaskProjectionStore(
                _taskService,
                historyService,
                new SystemClock());
            ListState = new DownloadListState();
            Notifications = new RecordingNotificationService();
            Dialogs = new StubDialogService(outcome);
            Policy = new DownloadDuplicatePolicy(
                ListState,
                _projectionStore,
                Dialogs);
        }

        public DownloadDuplicatePolicy Policy { get; }

        public DownloadTaskProjectionStore ProjectionStore => _projectionStore;

        public DownloadListState ListState { get; }

        public MutableDownloadTaskStore Store { get; }

        public RecordingNotificationService Notifications { get; }

        public StubDialogService Dialogs { get; }

        public static DuplicatePolicyContext WithCompleted(
            AppDialogOutcome outcome,
            bool loadUi = false,
            DownloadContentSelection? content = null)
        {
            var history = CreateCompletedHistory(content);
            var context = new DuplicatePolicyContext(outcome, history: history);
            if (loadUi)
            {
                context.ListState.AddDownloaded(
                    DownloadTaskProjectionMapper.ToDownloadedItem(history));
            }

            return context;
        }

        public static DownloadHistoryRecord CreateCompletedHistory(
            DownloadContentSelection? content = null)
        {
            var draft = CreateDownloadingItem();
            if (content != null)
            {
                draft.DownloadBase.NeedDownloadContent = content;
            }

            var queued = DownloadTaskProjectionMapper.CreateNewTask(
                draft,
                DateTimeOffset.UnixEpoch);
            var outputPath = typeof(DownloadAddOwnerTests).Assembly.Location;
            var artifacts = new Dictionary<string, string>();
            var finalizedContent = content ?? draft.DownloadBase.NeedDownloadContent;
            if (finalizedContent.HasMedia)
            {
                artifacts["media"] = outputPath;
            }

            if (finalizedContent.Subtitle)
            {
                artifacts["subtitle:test"] = outputPath;
            }

            var danmakuOutputFormat = finalizedContent.Danmaku
                ? finalizedContent.DanmakuOutputFormat
                  ?? DownloadDanmakuOutputFormat.Ass | DownloadDanmakuOutputFormat.Xml
                : DownloadDanmakuOutputFormat.None;
            if (danmakuOutputFormat.HasFlag(DownloadDanmakuOutputFormat.Ass))
            {
                artifacts[DownloadArtifactWriter.DanmakuAssTransferKey] = outputPath;
            }

            if (danmakuOutputFormat.HasFlag(DownloadDanmakuOutputFormat.Xml))
            {
                artifacts[DownloadArtifactWriter.DanmakuXmlTransferKey] = outputPath;
            }

            if (finalizedContent.Cover)
            {
                artifacts["cover"] = outputPath;
            }

            Assert.True(queued.UpdateOutput(
                    new DownloadOutput(queued.Output.BasePath, null, artifacts),
                    DateTimeOffset.UnixEpoch.AddMilliseconds(500))
                .TryGetValue(out var withOutput));
            Assert.True(withOutput.Start(DateTimeOffset.UnixEpoch.AddSeconds(1))
                .TryGetValue(out var started));
            Assert.True(started.Complete(
                new DownloadCompletion(2, "finished", null),
                DateTimeOffset.UnixEpoch.AddSeconds(2))
                .TryGetValue(out var completed));
            return DownloadHistoryRecord.FromCompletedTask(completed);
        }

        public void Dispose()
        {
            _projectionStore.Dispose();
            _taskService.Dispose();
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            PostCount++;
        }
    }

    private sealed class RecordingNotificationService : IUserNotificationService
    {
        public event EventHandler<UserNotificationEventArgs>? NotificationRaised;

        public List<string> Messages { get; } = [];

        public void Show(string message)
        {
            Messages.Add(message);
            NotificationRaised?.Invoke(this, new UserNotificationEventArgs(message));
        }
    }

    private sealed class StubDialogService(AppDialogOutcome outcome) : IAppDialogService
    {
        public int ShowCount { get; private set; }

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ShowCount++;
            return Task.FromResult(new AppDialogResult(
                outcome,
                new Dictionary<string, object?>()));
        }
    }

    private sealed class MutableDownloadTaskStore(
        DownloadTask? current,
        DownloadHistoryRecord? history) :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        public DownloadTask? Current { get; private set; } = current;

        public DownloadHistoryRecord? History { get; private set; } = history;

        public int UpdateCount { get; private set; }

        public int DeleteHistoryCount { get; private set; }

        public int HistoryPageRequestCount { get; private set; }

        public Task<DownloadHistoryPage>? PendingHistoryPage { get; set; }

        public DownloadTaskId? DeletedHistoryId { get; private set; }

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            History = history;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            History = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current?.Id == taskId ? Current : null);
        }

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryPageRequestCount++;
            if (PendingHistoryPage != null)
            {
                return PendingHistoryPage;
            }

            IReadOnlyList<DownloadHistoryRecord> items = History == null
                ? []
                : [History];
            return Task.FromResult(new DownloadHistoryPage(items, null));
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>([]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current == null || Current.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(new OperationError(
                    "download.store.conflict",
                    "Version mismatch.",
                    OperationErrorKind.Conflict)));
            }

            Current = task;
            UpdateCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            Current = null;
            History = history;
            UpdateCount++;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedHistoryId = taskId;
            DeleteHistoryCount++;
            if (History?.Id == taskId)
            {
                History = null;
            }

            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());
    }
}
