using System.Collections.Concurrent;
using System.Net.Http;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.FileName;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Presentation;
using DownKyi.Services;
using DownKyi.Services.Download;
using DownKyi.Services.Video;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;
using CoreVideoPage = DownKyi.Core.BiliApi.Video.Models.VideoPage;
using VideoPage = DownKyi.Presentation.VideoPage;

namespace DownKyi.Tests;

public sealed class VideoTagLoadingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-video-tag-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VideoPageUsesCurrentOperationAfterOriginalParseWasCanceled()
    {
        Directory.CreateDirectory(_directory);
        using var settings = new DownKyi.Core.Settings.SettingsStore(
            Path.Combine(_directory, "settings.json"));
        using var parseCancellation = new CancellationTokenSource();
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var provider = new RecordingTagProvider(
            (_, _, cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["tag"]));
        var videoView = new DownKyi.Core.BiliApi.Video.Models.VideoView
        {
            Aid = 42,
            Bvid = "BV1test",
            Title = "video",
            Pages =
            [
                new CoreVideoPage
                {
                    Cid = 84,
                    Page = 1,
                    Part = "page"
                }
            ]
        };
        var service = new VideoInfoService(
            videoView,
            settings,
            provider,
            new TestWbiKeyProvider(),
            new TestBilibiliApiClient());
        var page = Assert.Single(service.GetVideoPages(parseCancellation.Token)!);

        await parseCancellation.CancelAsync();
        var tags = await page.LoadTagsAsync(downloadCancellation.Token).ConfigureAwait(true);

        Assert.Equal(["tag"], tags);
        Assert.Equal(downloadCancellation.Token, provider.LastToken);
        Assert.NotEqual(parseCancellation.Token, provider.LastToken);
    }

    [Fact]
    public async Task AddToDownloadPassesCurrentOperationTokenToMetadataLoader()
    {
        using var context = CreateContext(generateMetadata: true);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        CancellationToken observedToken = default;
        var page = CreatePage(cancellationToken =>
        {
            observedToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<string>>(["current"]);
        });
        var preparedDownload = await context.PrepareAsync(page);

        var added = await context
            .AddToDownloadAsync(CreateSelection(_directory), preparedDownload, cancellationToken: operation.Token)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(operation.Token, observedToken);
        Assert.Equal("current", Assert.Single(Assert.Single(context.ListState.Downloading).Metadata!.Tags));
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task CancelingAddToDownloadCancelsTagRequestAndDoesNotCreateTask()
    {
        using var context = CreateContext(generateMetadata: true);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = CreatePage(async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(true);
            return Array.Empty<string>();
        });
        var preparedDownload = await context.PrepareAsync(page);

        var addTask = context.AddToDownloadAsync(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: operation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await operation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => addTask);
        Assert.Empty(context.ListState.Downloading);
        Assert.Equal(0, context.Store.AddCount);
    }

    [Fact]
    public async Task CancellationAfterPersistenceDoesNotStrandQueuedTask()
    {
        using var context = CreateContext(generateMetadata: false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Store.AfterAddAsync = operation.CancelAsync;
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        var added = await context
            .AddToDownloadAsync(CreateSelection(_directory), preparedDownload, cancellationToken: operation.Token)
            .ConfigureAwait(true);

        Assert.True(operation.IsCancellationRequested);
        Assert.Equal(1, added);
        Assert.Single(context.ListState.Downloading);
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task TagNetworkFailureDoesNotBlockDownloadTaskCreation()
    {
        using var context = CreateContext(generateMetadata: true);
        var page = CreatePage(_ => Task.FromException<IReadOnlyList<string>>(
            new HttpRequestException("tag endpoint unavailable")));
        var preparedDownload = await context.PrepareAsync(page);

        var added = await context
            .AddToDownloadAsync(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Empty(Assert.Single(context.ListState.Downloading).Metadata!.Tags);
        var warning = Assert.Single(context.Logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.IsType<HttpRequestException>(warning.Exception);
    }

    [Fact]
    public async Task SubtitleDiscoveryFailureStillOpensDownloadSettings()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromException<string>(
                new HttpRequestException("subtitle endpoint unavailable"))
        };
        using var context = CreateContext(generateMetadata: false, client: client);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));

        var selection = await context.Service.SelectDownloadAsync(
            page,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(selection);
        var request = Assert.Single(context.Dialogs.Requests);
        Assert.Equal(
            DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Failed,
            DownloadSettingsDialog.ReadSubtitleDiscovery(request).Status);
    }

    [Fact]
    public async Task MissingSubtitlePageIsTypedAsDiscoveryNotAttempted()
    {
        using var context = CreateContext(generateMetadata: false);

        var selection = await context.Service.SelectDownloadAsync(
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(selection);
        Assert.Equal(
            DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NotAttempted,
            DownloadSettingsDialog.ReadSubtitleDiscovery(
                Assert.Single(context.Dialogs.Requests)).Status);
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"aid\":1,\"bvid\":\"BV1test\",\"cid\":2,\"subtitle\":null}}")]
    [InlineData("{\"code\":0,\"data\":{\"aid\":1,\"bvid\":\"BV1test\",\"cid\":2,\"subtitle\":{\"subtitles\":null}}}")]
    public async Task MissingSubtitleContainerOpensDownloadSettingsWithNoTracks(string response)
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(response)
        };
        using var context = CreateContext(generateMetadata: false, client: client);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));

        var selection = await context.Service.SelectDownloadAsync(
            page,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(selection);
        var request = Assert.Single(context.Dialogs.Requests);
        Assert.Empty(DownloadSettingsDialog.ReadSubtitleTracks(request));
        Assert.Equal(
            DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NoResource,
            DownloadSettingsDialog.ReadSubtitleDiscovery(request).Status);
    }

    [Fact]
    public async Task SubtitleDiscoveryReturnsTypedAvailableTracksWithoutPlaybackDependency()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """
                {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/zh.json","type":0}]}}}
                """)
        };
        using var context = CreateContext(generateMetadata: false, client: client);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlaybackAvailability = null;

        var selection = await context.Service.SelectDownloadAsync(
            page,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(selection);
        var discovery = DownloadSettingsDialog.ReadSubtitleDiscovery(
            Assert.Single(context.Dialogs.Requests));
        Assert.Equal(DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.Available, discovery.Status);
        var track = Assert.Single(discovery.Tracks);
        Assert.Equal(11, track.TrackId);
        Assert.Equal("zh", track.Language);
    }

    [Fact]
    public async Task CanceledTagLoadIsNotPermanentlyCached()
    {
        var attempts = 0;
        var page = CreatePage(cancellationToken =>
        {
            attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>(["recovered"]);
        });
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => page.LoadTagsAsync(canceled.Token));
        var tags = await page.LoadTagsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, attempts);
        Assert.Equal(["recovered"], tags);
    }

    [Fact]
    public async Task EnabledMovieMetadataIncludesLoadedTags()
    {
        using var context = CreateContext(generateMetadata: true);
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>(["one", "two"])));

        var added = await context
            .AddToDownloadAsync(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(["one", "two"], Assert.Single(context.ListState.Downloading).Metadata!.Tags);
    }

    [Fact]
    public async Task MalformedOptionalApiTagsDoNotBlockDownloadTaskCreation()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = static (_, _) => Task.FromResult(
                """
                {
                  "code": 0,
                  "data": [
                    { "tag_id": 1, "tag_name": null },
                    { "tag_id": 2, "tag_name": "" },
                    { "tag_id": 3, "tag_name": "   " },
                    { "tag_id": 4, "tag_name": "kept" }
                  ]
                }
                """)
        };
        var provider = new VideoTagProvider(client);
        using var context = CreateContext(generateMetadata: true);
        var preparedDownload = await context.PrepareAsync(CreatePage(cancellationToken => provider.GetTagsAsync(
            "BV1test",
            84,
            cancellationToken)));

        var added = await context
            .AddToDownloadAsync(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(["kept"], Assert.Single(context.ListState.Downloading).Metadata!.Tags);
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task DisabledMovieMetadataDoesNotLoadTags()
    {
        using var context = CreateContext(generateMetadata: false);
        var loadCount = 0;
        var preparedDownload = await context.PrepareAsync(CreatePage(_ =>
        {
            loadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["unused"]);
        }));

        var added = await context
            .AddToDownloadAsync(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(0, loadCount);
        Assert.Null(Assert.Single(context.ListState.Downloading).Metadata);
    }

    [Fact]
    public async Task ResolverFailureIsReportedAndDoesNotBlockNextAdmission()
    {
        var resolver = new FailFirstPhysicalOutputPathResolver();
        using var context = CreateContext(generateMetadata: false, resolver);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Cid = 1;
        first.Name = "first";
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Cid = 2;
        second.Name = "second";
        var preparedDownload = await context.PrepareAsync(first, second);

        var added = await context
            .AddToDownloadAsync(
                CreateSelection(_directory),
                preparedDownload,
                cancellationToken: TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(1, added);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(1, context.Store.AddCount);
        Assert.Equal("second", Assert.Single(context.ListState.Downloading).DownloadBase.Name);
        Assert.Single(context.Queue.Enqueued);
        var request = Assert.Single(context.Dialogs.Requests);
        Assert.Equal(AppDialog.Alert, request.Dialog);
        var message = Assert.IsType<string>(request.Parameters!["message"]);
        Assert.Equal(DictionaryResource.GetString("DirectoryError"), message);
        Assert.DoesNotContain(_directory, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownResolverFailureKeepsExistingFailureBehavior()
    {
        using var context = CreateContext(
            generateMetadata: false,
            new UnknownFailurePhysicalOutputPathResolver());
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.AddToDownloadAsync(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, context.Store.AddCount);
        Assert.Empty(context.ListState.Downloading);
        Assert.Empty(context.Queue.Enqueued);
        Assert.Empty(context.Dialogs.Requests);
    }

    [Fact]
    public async Task RuntimeFailureStopsAdmissionAcrossRemainingSections()
    {
        using var context = CreateContext(
            generateMetadata: false,
            runtimeAvailability: new UnavailableDownloadRuntimeAvailability());
        var preparedDownload = await context.PrepareSectionsAsync(
            [CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]))],
            [CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]))]);

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory),
            preparedDownload,
            isAll: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, added);
        Assert.Equal(0, context.Store.AddCount);
        Assert.Empty(context.ListState.Downloading);
        Assert.Single(context.Dialogs.Requests);
    }

    [Fact]
    public async Task MultiPageAdmissionReadsCompletedHistoryOnce()
    {
        using var context = CreateContext(generateMetadata: false);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Cid = 1;
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Cid = 2;
        var preparedDownload = await context.PrepareAsync(first, second);

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, added);
        Assert.Equal(1, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task ExistingDataPreparationReportsEachPagesMediaCapabilities()
    {
        using var context = CreateContext(generateMetadata: false);
        var videoOnly = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(videoOnly, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video-only.m4s"
                    }
                ]
            }
        });
        var audioAndVideo = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(audioAndVideo, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/video.m4s"
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
        var combined = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(combined, new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://media.invalid/combined.mp4"
                }
            ]
        }, isDurl: true);
        var dolby = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(dolby, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/dolby-video.m4s"
                    }
                ],
                Dolby = new PlayUrlDashDolby
                {
                    Audio =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30250,
                            BaseAddress = "https://media.invalid/dolby-audio.m4s"
                        }
                    ]
                }
            }
        });
        var flac = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(flac, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/flac-video.m4s"
                    }
                ],
                Flac = new PlayUrlDashFlac
                {
                    Audio = new PlayUrlDashVideo
                    {
                        Id = 30251,
                        BaseAddress = "https://media.invalid/audio.flac"
                    }
                }
            }
        });
        var addressless = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(addressless, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video = [new PlayUrlDashVideo { Id = 80, CodecId = 7 }]
            }
        });

        var preparedDownload = await context.PrepareAsync(
            videoOnly,
            audioAndVideo,
            combined,
            dolby,
            flac,
            addressless);
        var pages = Assert.Single(preparedDownload.Sections).Pages;

        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
            pages[0].AvailableMedia);
        Assert.Equal(
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioOnly |
                DownloadMediaOutputModes.AudioVideo),
            pages[1].AvailableMedia);
        Assert.Equal(
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioVideo),
            pages[2].AvailableMedia);
        Assert.False(pages[2].AvailableMedia.Supports(
            DownloadContentSelection.None with { Audio = true }));
        Assert.Equal(
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioOnly |
                DownloadMediaOutputModes.AudioVideo),
            pages[3].AvailableMedia);
        Assert.Equal(
            new DownloadMediaCapabilities(
                DownloadMediaOutputModes.VideoOnly |
                DownloadMediaOutputModes.AudioOnly |
                DownloadMediaOutputModes.AudioVideo),
            pages[4].AvailableMedia);
        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.None),
            pages[5].AvailableMedia);
    }

    [Fact]
    public async Task ExplicitRequestedContentIsStoredWithoutDirectorySelectionState()
    {
        using var context = CreateContext(generateMetadata: false);
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));
        var requestedContent = DownloadContentSelection.None with
        {
            Video = true,
            Subtitle = true
        };

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory, requestedContent),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, added);
        Assert.Equal(
            requestedContent with { MediaKind = DownloadMediaKind.Durl },
            Assert.Single(context.ListState.Downloading).DownloadBase.NeedDownloadContent);
    }

    [Fact]
    public async Task SubtitleSupplementKeepsTheMediaBasePathThroughTheCompleteAddFlow()
    {
        using var context = CreateContext(generateMetadata: false);
        context.UseContentIndependentFileName();
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));
        var media = DownloadContentSelection.None with { Video = true };

        Assert.Equal(1, await context.AddToDownloadAsync(
            CreateSelection(_directory, media),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));
        var mediaItem = Assert.Single(context.ListState.Downloading);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaItem.DownloadBase.FilePath)!);
        await File.WriteAllTextAsync(
            mediaItem.DownloadBase.FilePath + ".mp4",
            "existing-media",
            TestContext.Current.CancellationToken);

        var subtitle = DownloadContentSelection.None with { Subtitle = true };
        Assert.Equal(1, await context.AddToDownloadAsync(
            CreateSelection(_directory, subtitle),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        var subtitleItem = Assert.Single(
            context.ListState.Downloading,
            item => item.DownloadBase.NeedDownloadContent.HasSubtitleAction);
        Assert.Equal(mediaItem.DownloadBase.FilePath, subtitleItem.DownloadBase.FilePath);
        Assert.DoesNotContain("(1)", subtitleItem.DownloadBase.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtitleSupplementUsesOnePhysicalOwnerAcrossPathAliases()
    {
        var aliasRoot = Path.Combine(_directory, $"alias-{Guid.NewGuid():N}");
        var logicalDirectory = Path.Combine(aliasRoot, "logical");
        var physicalDirectory = Path.Combine(aliasRoot, "physical");
        using var context = CreateContext(
            generateMetadata: false,
            new AliasPhysicalOutputPathResolver(logicalDirectory, physicalDirectory));
        context.UseContentIndependentFileName();
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        Assert.Equal(1, await context.AddToDownloadAsync(
            CreateSelection(
                logicalDirectory,
                DownloadContentSelection.None with { Video = true }),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));
        var mediaItem = Assert.Single(context.ListState.Downloading);
        Assert.StartsWith(
            Path.GetFullPath(physicalDirectory),
            mediaItem.DownloadBase.FilePath,
            StringComparison.Ordinal);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaItem.DownloadBase.FilePath)!);
        await File.WriteAllTextAsync(
            mediaItem.DownloadBase.FilePath + ".mp4",
            "existing-media",
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await context.AddToDownloadAsync(
            CreateSelection(
                logicalDirectory,
                DownloadContentSelection.None with { Subtitle = true }),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        var subtitleItem = Assert.Single(
            context.ListState.Downloading,
            item => item.DownloadBase.NeedDownloadContent.HasSubtitleAction);
        Assert.Equal(mediaItem.DownloadBase.FilePath, subtitleItem.DownloadBase.FilePath);
        Assert.DoesNotContain("(1)", subtitleItem.DownloadBase.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AliasedSubtitleConflictDoesNotOverwriteTheExistingSubtitle()
    {
        var aliasRoot = Path.Combine(_directory, $"conflict-{Guid.NewGuid():N}");
        var logicalDirectory = Path.Combine(aliasRoot, "logical");
        var physicalDirectory = Path.Combine(aliasRoot, "physical");
        using var context = CreateContext(
            generateMetadata: false,
            new AliasPhysicalOutputPathResolver(logicalDirectory, physicalDirectory));
        context.UseContentIndependentFileName();
        var preparedDownload = await context.PrepareAsync(
            CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([])));

        Assert.Equal(1, await context.AddToDownloadAsync(
            CreateSelection(
                logicalDirectory,
                DownloadContentSelection.None with { Video = true }),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));
        var mediaItem = Assert.Single(context.ListState.Downloading);
        Directory.CreateDirectory(Path.GetDirectoryName(mediaItem.DownloadBase.FilePath)!);
        var subtitlePath = mediaItem.DownloadBase.FilePath + ".srt";
        await File.WriteAllTextAsync(
            subtitlePath,
            "existing-subtitle",
            TestContext.Current.CancellationToken);

        Assert.Equal(0, await context.AddToDownloadAsync(
            CreateSelection(
                logicalDirectory,
                DownloadContentSelection.None with { Subtitle = true }),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(
            "existing-subtitle",
            await File.ReadAllTextAsync(
                subtitlePath,
                TestContext.Current.CancellationToken));
        Assert.Single(context.ListState.Downloading);
    }

    [Fact]
    public async Task ConflictChoiceIsStoredAsThePageRequestedContent()
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(page, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/conflict-video.m4s"
                    }
                ],
                Audio = []
            }
        });
        var preparedDownload = await context.PrepareAsync(page);
        context.Dialogs.Result = new AppDialogResult(
            AppDialogOutcome.Accepted,
            DownloadContentConflictDialogContract.Encode(new DownloadContentConflictDecision(
                DownloadContentConflictAction.UseAvailableContent,
                ApplyToAll: false)));

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory, DownloadContentSelection.All),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, added);
        Assert.Equal(
            DownloadContentSelection.All with
            {
                Audio = false,
                MediaKind = DownloadMediaKind.Dash,
                DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
            },
            Assert.Single(context.ListState.Downloading).DownloadBase.NeedDownloadContent);
    }

    [Fact]
    public async Task ConflictSkipDoesNotCreateDownloadTask()
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        SetPlayback(page, new PlayUrl
        {
            Dash = new PlayUrlDash
            {
                Video =
                [
                    new PlayUrlDashVideo
                    {
                        Id = 80,
                        CodecId = 7,
                        BaseAddress = "https://media.invalid/skip-video.m4s"
                    }
                ],
                Audio = []
            }
        });
        var preparedDownload = await context.PrepareAsync(page);
        context.Dialogs.Result = new AppDialogResult(
            AppDialogOutcome.Accepted,
            DownloadContentConflictDialogContract.Encode(new DownloadContentConflictDecision(
                DownloadContentConflictAction.SkipPage,
                ApplyToAll: false)));

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory, DownloadContentSelection.All),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, added);
        Assert.Empty(context.ListState.Downloading);
        Assert.Equal(0, context.Store.AddCount);
    }

    [Fact]
    public async Task InfoServicePreparationResolvesPlaybackIntoTheSamePreparedResult()
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlaybackAvailability = null;
        var video = new VideoInfoView { Title = "resolved" };
        var section = new VideoSection { VideoPages = [page] };
        var infoService = new PreparationInfoService(
            video,
            [section],
            new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 80,
                            CodecId = 7,
                            BaseAddress = "https://media.invalid/resolved-video.m4s"
                        }
                    ]
                }
            });

        var preparedDownload = await context.Service.PrepareAsync(
            infoService,
            DownloadContentSelection.All,
            TestContext.Current.CancellationToken);

        Assert.NotNull(preparedDownload);
        Assert.Same(video, preparedDownload.Video);
        var preparedPage = Assert.Single(Assert.Single(preparedDownload.Sections).Pages);
        Assert.Same(page, preparedPage.Page);
        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
            preparedPage.AvailableMedia);
        Assert.True(page.IsSelected);
        Assert.Equal(1, infoService.StreamRequestCount);
    }

    [Fact]
    public async Task MissingVideoQualityDoesNotRepeatAnIdenticalPlaybackQuery()
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlaybackAvailability = null;
        page.VideoQuality = null;
        var infoService = new PreparationInfoService(
            new VideoInfoView { Title = "missing-quality" },
            [new VideoSection { VideoPages = [page] }],
            new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 0,
                            CodecId = 7,
                            BaseAddress = "https://media.invalid/missing-quality.m4s"
                        }
                    ]
                }
            });

        var prepared = await context.Service.PrepareAsync(
            infoService,
            DownloadContentSelection.None with { Video = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared);
        Assert.Equal(1, infoService.StreamRequestCount);
        Assert.Null(page.VideoQuality);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task IndependentContentPreparationDoesNotRequestPlayback(
        bool subtitle,
        bool danmaku,
        bool cover)
    {
        using var context = CreateContext(generateMetadata: false);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlaybackAvailability = null;
        page.VideoQuality = null;
        var infoService = new PreparationInfoService(
            new VideoInfoView { Title = "sidecar-only" },
            [new VideoSection { VideoPages = [page] }],
            new PlayUrl());
        var requested = DownloadContentSelection.None with
        {
            Subtitle = subtitle,
            Danmaku = danmaku,
            Cover = cover
        };

        var prepared = await context.Service.PrepareAsync(
            infoService,
            requested,
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared);
        Assert.Equal(0, infoService.StreamRequestCount);
        Assert.False(Assert.Single(Assert.Single(prepared.Sections).Pages).AvailableMedia.HasAnyMedia);
    }

    private DownloadTestContext CreateContext(
        bool generateMetadata,
        IPhysicalOutputPathResolver? resolver = null,
        IDownloadTaskQueue? taskQueue = null,
        IDownloadRuntimeAvailability? runtimeAvailability = null,
        TestBilibiliApiClient? client = null)
    {
        Directory.CreateDirectory(_directory);
        return new DownloadTestContext(
            Path.Combine(_directory, $"settings-{Guid.NewGuid():N}.json"),
            generateMetadata,
            resolver,
            taskQueue,
            runtimeAvailability,
            client);
    }

    private static DownloadAddSelection CreateSelection(
        string directory,
        DownloadContentSelection? requestedContent = null)
    {
        return new DownloadAddSelection(
            directory,
            requestedContent ?? DownloadContentSelection.All);
    }

    private static VideoPage CreatePage(
        Func<CancellationToken, Task<IReadOnlyList<string>>> loadTagsAsync)
    {
        var page = new VideoPage
        {
            Avid = 42,
            Bvid = "BV1test",
            Cid = 84,
            EpisodeId = -1,
            IsSelected = true,
            Name = "page",
            Order = 1,
            OriginalPublishTime = new DateTime(2024, 1, 2),
            PublishTime = "2024-01-02",
            VideoQuality = new VideoQuality
            {
                Quality = 80,
                QualityFormat = "1080P",
                IsDurl = true,
                SelectedVideoCodec = "H.264/AVC"
            },
            LoadTagsAsync = loadTagsAsync
        };
        SetPlayback(page, new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl =
            [
                new PlayUrlDurl
                {
                    Order = 1,
                    SourceAddress = "https://media.invalid/default.mp4"
                }
            ]
        }, isDurl: true);
        return page;
    }

    private static void SetPlayback(VideoPage page, PlayUrl playUrl, bool isDurl = false)
    {
        page.PlaybackAvailability = PlayUrlAvailability.From(playUrl);
        var firstAudioId = page.PlaybackAvailability.Audio.Count > 0
            ? page.PlaybackAvailability.Audio[0]
            : 0;
        page.AudioQualityFormat = PlaybackQualityCatalog.GetAudioQualities()
            .FirstOrDefault(audio => audio.Id == firstAudioId)
            ?.Name ?? string.Empty;
        page.VideoQuality = new VideoQuality
        {
            Quality = 80,
            QualityFormat = "1080P",
            IsDurl = isDurl,
            SelectedVideoCodec = "H.264/AVC"
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class DownloadTestContext : IDisposable
    {
        private readonly DownKyi.Core.Settings.SettingsStore _settings;
        private readonly DownloadTaskApplicationService _taskService;
        private readonly DownloadTaskProjectionStore _projectionStore;
        private readonly DownloadTaskAdmissionService _admission;

        public DownloadTestContext(
            string settingsPath,
            bool generateMetadata,
            IPhysicalOutputPathResolver? resolver,
            IDownloadTaskQueue? taskQueue,
            IDownloadRuntimeAvailability? runtimeAvailability,
            TestBilibiliApiClient? client)
        {
            _settings = new DownKyi.Core.Settings.SettingsStore(settingsPath);
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    Content = settings.Video.Content with
                    {
                        GenerateMovieMetadata = generateMetadata
                    }
                }
            });
            Store = new RecordingDownloadTaskStore();
            var clock = new SystemClock();
            var historyService = DownloadHistoryService.CreateForSharedStore(Store);
            _taskService = new DownloadTaskApplicationService(Store, historyService, clock);
            _projectionStore = new DownloadTaskProjectionStore(
                _taskService,
                historyService,
                clock);
            ListState = new DownloadListState();
            Queue = new RecordingDownloadTaskQueue();
            Logger = new RecordingLogger<DownloadMovieMetadataBuilder>();
            Dialogs = new RecordingDialogService();
            client ??= new TestBilibiliApiClient();
            var physicalOutputPathResolver = resolver
                ?? new FileSystemPhysicalOutputPathResolver();
            _admission = new DownloadTaskAdmissionService(
                ListState,
                _taskService,
                _projectionStore,
                new DownloadTaskStateWriter(_taskService),
                taskQueue ?? Queue,
                runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
                physicalOutputPathResolver);
            var duplicatePolicy = new DownloadDuplicatePolicy(
                ListState,
                _projectionStore,
                physicalOutputPathResolver,
                Dialogs);
            Service = new AddToDownloadService(
                DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video,
                _admission,
                new LegacyDownloadAdmissionPresenter(_taskService, Dialogs),
                duplicatePolicy,
                new DownloadMovieMetadataBuilder(Logger),
                _settings,
                new VideoTagProvider(client),
                new TestWbiKeyProvider(),
                client,
                Dialogs,
                new RecordingLogger<AddToDownloadService>());
        }

        public AddToDownloadService Service { get; }

        public DownloadListState ListState { get; }

        public RecordingDownloadTaskStore Store { get; }

        public RecordingDownloadTaskQueue Queue { get; }

        public RecordingLogger<DownloadMovieMetadataBuilder> Logger { get; }

        public RecordingDialogService Dialogs { get; }

        public void UseContentIndependentFileName()
        {
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    FileNameParts =
                    [
                        FileNamePart.MainTitle,
                        FileNamePart.Slash,
                        FileNamePart.PageTitle
                    ]
                }
            });
        }

        public async Task<int> AddToDownloadAsync(
            DownloadAddSelection selection,
            PreparedDownload preparedDownload,
            bool isAll = false,
            CancellationToken cancellationToken = default)
        {
            var finalizedDownload = await new DownloadActionPlanner(
                    new DownloadContentConflictResolver(Dialogs))
                .PlanAsync(
                    selection.RequestedContent,
                    preparedDownload,
                    isAll,
                    new DownloadContentConflictChoices(),
                    cancellationToken)
                .ConfigureAwait(true);
            var result = await Service
                .AddToDownload(selection.Directory, finalizedDownload, cancellationToken)
                .ConfigureAwait(true);
            return result.AddedCount;
        }

        public Task<PreparedDownload> PrepareAsync(params VideoPage[] pages)
        {
            return Service.PrepareAsync(
                new VideoInfoView
                {
                    Title = "video",
                    Description = "description",
                    VideoZone = "Technology"
                },
                [
                    new VideoSection
                    {
                        Id = 1,
                        IsSelected = true,
                        Title = "section",
                        VideoPages = pages
                    }
                ],
                DownloadContentSelection.All,
                isAll: false,
                TestContext.Current.CancellationToken);
        }

        public Task<PreparedDownload> PrepareSectionsAsync(params VideoPage[][] sections)
        {
            return Service.PrepareAsync(
                new VideoInfoView
                {
                    Title = "video",
                    Description = "description",
                    VideoZone = "Technology"
                },
                sections.Select((pages, index) => new VideoSection
                {
                    Id = index + 1,
                    IsSelected = true,
                    Title = $"section-{index + 1}",
                    VideoPages = pages
                }).ToArray(),
                DownloadContentSelection.All,
                isAll: true,
                TestContext.Current.CancellationToken);
        }

        public void Dispose()
        {
            _admission.Dispose();
            _projectionStore.Dispose();
            _taskService.Dispose();
            _settings.Dispose();
        }
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public List<AppDialogRequest> Requests { get; } = [];

        public AppDialogResult Result { get; set; } = new(
            AppDialogOutcome.Canceled,
            new Dictionary<string, object?>());

        public Task<AppDialogResult> ShowAsync(
            AppDialogRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class FailFirstPhysicalOutputPathResolver : IPhysicalOutputPathResolver
    {
        public int CallCount { get; private set; }

        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            CallCount++;
            if (CallCount == 1)
            {
                throw new IOException("Unable to resolve physical output path.");
            }

            return logicalBasePath;
        }
    }

    private sealed class UnknownFailurePhysicalOutputPathResolver : IPhysicalOutputPathResolver
    {
        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            throw new InvalidOperationException("Unexpected resolver failure.");
        }
    }

    private sealed class AliasPhysicalOutputPathResolver(
        string logicalRoot,
        string physicalRoot) : IPhysicalOutputPathResolver
    {
        private readonly string _logicalRoot = Path.GetFullPath(logicalRoot);
        private readonly string _physicalRoot = Path.GetFullPath(physicalRoot);

        public string ResolvePhysicalBasePath(string logicalBasePath)
        {
            var fullPath = Path.GetFullPath(logicalBasePath);
            var relativePath = Path.GetRelativePath(_logicalRoot, fullPath);
            return relativePath == ".."
                   || relativePath.StartsWith(
                       ".." + Path.DirectorySeparatorChar,
                       StringComparison.Ordinal)
                ? fullPath
                : Path.GetFullPath(Path.Combine(_physicalRoot, relativePath));
        }
    }

    private sealed class UnavailableDownloadRuntimeAvailability : IDownloadRuntimeAvailability
    {
        public void EnsureAcceptingTasks()
        {
            throw new DownloadRuntimeUnavailableException(
                "Synthetic unavailable download runtime.");
        }

        public Task<DownloadRuntimeStartupOutcome> WaitForStartupOutcomeAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadRuntimeStartupOutcome.Faulted(
                new DownloadRuntimeUnavailableException(
                    "Synthetic unavailable download runtime.")));
        }
    }

    private sealed class RecordingTagProvider(
        Func<string, long, CancellationToken, Task<IReadOnlyList<string>>> loadTagsAsync)
        : IVideoTagProvider
    {
        public CancellationToken LastToken { get; private set; }

        public Task<IReadOnlyList<string>> GetTagsAsync(
            string bvid,
            long cid,
            CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            return loadTagsAsync(bvid, cid, cancellationToken);
        }
    }

    private sealed class PreparationInfoService(
        VideoInfoView video,
        IList<VideoSection> sections,
        PlayUrl playUrl) : IInfoService
    {
        public int StreamRequestCount { get; private set; }

        public VideoInfoView? GetVideoView(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return video;
        }

        public IList<VideoSection>? GetVideoSections(
            bool noUgc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(noUgc);
            return sections;
        }

        public IList<VideoPage>? GetVideoPages(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlayUrl?> GetVideoStreamAsync(
            VideoPage page,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StreamRequestCount++;
            return Task.FromResult<PlayUrl?>(playUrl);
        }
    }

    private sealed class RecordingDownloadTaskStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        public int AddCount { get; private set; }

        public int HistoryPageRequestCount { get; private set; }

        public Func<Task>? AfterAddAsync { get; set; }

        public async Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            if (AfterAddAsync != null)
            {
                await AfterAddAsync().ConfigureAwait(false);
            }

            return OperationResult.Success();
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) => Task.FromResult<DownloadTask?>(null);

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            HistoryPageRequestCount++;
            return Task.FromResult(new DownloadHistoryPage([], null));
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DownloadTask>>(
                Array.Empty<DownloadTask>());

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) => Task.FromResult(OperationResult.Success());
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
