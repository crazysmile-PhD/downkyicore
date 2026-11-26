using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Web;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Application.FileName;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.Video.Models;
using DownKyi.Core.BiliApi.VideoStream;
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
using Newtonsoft.Json;
using CoreVideoPage = DownKyi.Core.BiliApi.Video.Models.VideoPage;
using VideoPage = DownKyi.Presentation.VideoPage;

namespace DownKyi.Tests;

public sealed class VideoTagLoadingTests : IDisposable
{
    // Admission stores physical paths; temp directories can have aliased ancestors.
    private readonly string _directory = new FileSystemPhysicalOutputPathResolver()
        .ResolvePhysicalBasePath(Path.Combine(
            Path.GetTempPath(),
            "downkyi-video-tag-tests",
            Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task VideoPageUsesCurrentOperationAfterOriginalParseWasCanceled()
    {
        Directory.CreateDirectory(_directory);
        using var settings = new DownKyi.Infrastructure.Settings.SettingsStore(
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
    public async Task CancellationAfterFirstSubmissionCarriesTheSubmittedCount()
    {
        using var context = CreateContext(generateMetadata: false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        context.Store.AfterAddAsync = operation.CancelAsync;
        var firstPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        var secondPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        secondPage.Cid++;
        secondPage.Name = "page-2";
        secondPage.Order++;
        var preparedDownload = await context.PrepareAsync(firstPage, secondPage);

        var exception = await Assert.ThrowsAsync<DownloadAddCanceledException>(() =>
            context.AddToDownloadResultAsync(
                CreateSelection(_directory),
                preparedDownload,
                isAll: true,
                cancellationToken: operation.Token));

        Assert.Equal(DownloadAddOutcome.Canceled, exception.PartialResult.Outcome);
        Assert.Equal(1, exception.PartialResult.AddedCount);
        Assert.Single(context.ListState.Downloading);
        Assert.Single(context.Queue.Enqueued);
    }

    [Fact]
    public async Task FailureAfterFirstSubmissionReturnsPartialCounts()
    {
        using var context = CreateContext(generateMetadata: false);
        context.Store.ExceptionOnAddCall = 2;
        var firstPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        var secondPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        secondPage.Cid++;
        secondPage.Name = "page-2";
        secondPage.Order++;
        var preparedDownload = await context.PrepareAsync(firstPage, secondPage);

        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory),
            preparedDownload,
            isAll: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DownloadAddOutcome.Partial, result.Outcome);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.FailedCount);
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

        Assert.Equal(DownloadSelectionOutcome.Canceled, selection.Outcome);
        Assert.Null(selection.Selection);
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

        Assert.Equal(DownloadSelectionOutcome.Canceled, selection.Outcome);
        Assert.Null(selection.Selection);
        Assert.Equal(
            DownloadSettingsDialog.SubtitleTrackDiscoveryStatus.NotAttempted,
            DownloadSettingsDialog.ReadSubtitleDiscovery(
                Assert.Single(context.Dialogs.Requests)).Status);
    }

    [Fact]
    public async Task LegacyDefaultDirectoryStillOpensDownloadSelection()
    {
        using var context = CreateContext(generateMetadata: false);
        context.UseLegacyDefaultDirectory(_directory);
        context.Dialogs.Result = new AppDialogResult(
            AppDialogOutcome.Accepted,
            DownloadSettingsDialog.EncodeResult(
                _directory,
                DownloadContentSelection.All,
                selectedTrackIds: null,
                defaultTrackId: null));

        var result = await context.Service.SelectDownloadAsync(
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(DownloadSelectionOutcome.Accepted, result.Outcome);
        Assert.NotNull(Assert.IsType<DownloadAddSelection>(result.Selection));
        Assert.Single(context.Dialogs.Requests);
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

        Assert.Equal(DownloadSelectionOutcome.Canceled, selection.Outcome);
        Assert.Null(selection.Selection);
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

        Assert.Equal(DownloadSelectionOutcome.Canceled, selection.Outcome);
        Assert.Null(selection.Selection);
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
    public async Task CompletedDuplicatesAreConfirmedOnceBeforeMetadataIsLoaded()
    {
        using var context = CreateContext(generateMetadata: true);
        var requestedContent = DownloadContentSelection.None with { Video = true };
        var firstLoadCount = 0;
        var first = CreatePage(_ =>
        {
            firstLoadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["unused"]);
        });
        first.Cid = 1;
        first.Name = "first";
        var secondLoadCount = 0;
        var second = CreatePage(_ =>
        {
            secondLoadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["unused"]);
        });
        second.Cid = 2;
        second.Name = "second";
        second.Order = 2;
        await context.Store.AddHistoryAsync(
            CreateHistory(first, requestedContent),
            TestContext.Current.CancellationToken);
        await context.Store.AddHistoryAsync(
            CreateHistory(second, requestedContent),
            TestContext.Current.CancellationToken);
        context.Dialogs.Result = new AppDialogResult(
            AppDialogOutcome.Accepted,
            DownloadDuplicateBatchDialogContract.Encode(
                DownloadDuplicateBatchAction.SkipCompleted));
        var preparedDownload = await context.PrepareAsync(requestedContent, first, second);

        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory, requestedContent),
            preparedDownload,
            isAll: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(2, result.DuplicateCount);
        Assert.Equal(0, firstLoadCount);
        Assert.Equal(0, secondLoadCount);
        Assert.Equal(0, context.Store.AddCount);
        var request = Assert.Single(context.Dialogs.Requests);
        Assert.Equal(AppDialog.DownloadDuplicateBatch, request.Dialog);
        var prompt = Assert.IsType<DownloadDuplicateBatchPrompt>(
            request.Parameters![DownloadDuplicateBatchDialogContract.PromptParameter]);
        Assert.Equal(2, prompt.CompletedItemNames.Count);
    }

    [Fact]
    public async Task RedownloadDecisionLoadsMetadataAndAdmitsTheCompletedDuplicate()
    {
        using var context = CreateContext(generateMetadata: true);
        context.UseContentIndependentFileName();
        var requestedContent = DownloadContentSelection.None with { Video = true };
        var loadCount = 0;
        var page = CreatePage(_ =>
        {
            loadCount++;
            return Task.FromResult<IReadOnlyList<string>>(["kept"]);
        });
        await context.Store.AddHistoryAsync(
            CreateHistory(page, requestedContent),
            TestContext.Current.CancellationToken);
        context.Dialogs.Result = new AppDialogResult(
            AppDialogOutcome.Accepted,
            DownloadDuplicateBatchDialogContract.Encode(
                DownloadDuplicateBatchAction.RedownloadCompleted));
        var preparedDownload = await context.PrepareAsync(requestedContent, page);
        var preparedPage = Assert.Single(Assert.Single(preparedDownload.Sections).Pages);
        var oldDirectory = Path.Combine(_directory, preparedPage.Video.Title);
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, page.Name + ".mp4");
        await File.WriteAllTextAsync(oldPath, "previous-quality", TestContext.Current.CancellationToken);

        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory, requestedContent),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(1, loadCount);
        var download = Assert.Single(context.ListState.Downloading);
        Assert.Equal(["kept"], download.Metadata!.Tags);
        Assert.Equal(Path.Combine(oldDirectory, page.Name + "(1)"), download.DownloadBase.FilePath);
        Assert.Equal("previous-quality", await File.ReadAllTextAsync(oldPath, TestContext.Current.CancellationToken));
        Assert.Single(context.Dialogs.Requests);
    }

    [Fact]
    public async Task TemplateOnlyPagesShareTheCreatorDirectoryAndPreserveExistingFiles()
    {
        using var context = CreateContext(generateMetadata: false);
        context.SetFileNameTemplate(
            FileNamePart.UpName, FileNamePart.Slash,
            FileNamePart.Order, FileNamePart.Hyphen, FileNamePart.PageTitle);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Owner = new() { Mid = 42, Name = "creator" };
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Owner = first.Owner;
        second.Cid = first.Cid + 1;
        second.Order = 2;
        var creatorDirectory = Path.Combine(_directory, "creator");
        Directory.CreateDirectory(creatorDirectory);
        var oldPath = Path.Combine(creatorDirectory, "1-page.mp4");
        await File.WriteAllTextAsync(oldPath, "old-video", TestContext.Current.CancellationToken);
        var prepared = await context.PrepareAsync(first, second);

        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory), prepared, isAll: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(
            [Path.Combine(creatorDirectory, "1-page(1)"), Path.Combine(creatorDirectory, "2-page")],
            context.ListState.Downloading.Select(item => item.DownloadBase.FilePath));
        Assert.Equal("old-video", await File.ReadAllTextAsync(oldPath, TestContext.Current.CancellationToken));
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
    public async Task MultiPageAdmissionReviewsAllCidsInOneBatchBeforeCommit()
    {
        using var context = CreateContext(generateMetadata: false);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        first.Cid = 1;
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        second.Cid = 2;
        second.Name = "page-2";
        second.Order = 2;
        var preparedDownload = await context.PrepareAsync(first, second);

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory),
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, added);
        Assert.Equal(1, context.Store.UnfinishedByCidsRequestCount);
        Assert.Equal(1, context.Store.HistoryByCidsRequestCount);
        Assert.Equal(2, context.Store.UnfinishedByCidRequestCount);
        Assert.Equal(2, context.Store.HistoryByCidRequestCount);
        Assert.Equal(0, context.Store.HistoryPageRequestCount);
    }

    [Fact]
    public async Task SameBatchDuplicateIsSkippedWithoutOpeningAConfirmation()
    {
        using var context = CreateContext(generateMetadata: false);
        var first = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        var second = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        var preparedDownload = await context.PrepareAsync(first, second);

        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory),
            preparedDownload,
            isAll: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Equal(1, context.Store.AddCount);
        Assert.Single(context.ListState.Downloading);
        Assert.Empty(context.Dialogs.Requests);
    }

    [Fact]
    public async Task ActiveDuplicateIsSkippedWithoutOpeningAConfirmation()
    {
        using var context = CreateContext(generateMetadata: false);
        var requestedContent = DownloadContentSelection.None with { Video = true };
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        var preparedDownload = await context.PrepareAsync(requestedContent, page);
        var selection = CreateSelection(_directory, requestedContent);
        Assert.Equal(1, await context.AddToDownloadAsync(
            selection,
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken));

        var result = await context.AddToDownloadResultAsync(
            selection,
            preparedDownload,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.DuplicateCount);
        Assert.Empty(context.Dialogs.Requests);
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
            DownloadContentSelection.None,
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
    public async Task BangumiBatchAssignsTheSeriesCoverOnlyToTheFirstCoverTask()
    {
        using var context = CreateContext(generateMetadata: false);
        context.UseContentIndependentFileName();
        var firstPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        firstPage.EpisodeId = 101;
        firstPage.FirstFrame = "https://example.test/episode-1.jpg";
        var secondPage = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        secondPage.Cid++;
        secondPage.EpisodeId = 102;
        secondPage.Name = "page-2";
        secondPage.Order = 2;
        secondPage.FirstFrame = "https://example.test/episode-2.jpg";
        var requestedContent = DownloadContentSelection.None with { Cover = true };
        var preparedDownload = await context.Service.PrepareAsync(
            new VideoInfoView
            {
                Title = "bangumi",
                TypeId = 13,
                VideoZone = "Anime",
                CoverUrl = "https://example.test/series.jpg"
            },
            [
                new VideoSection
                {
                    Id = 1,
                    IsSelected = true,
                    Title = "episodes",
                    VideoPages = [firstPage, secondPage]
                }
            ],
            requestedContent,
            isAll: true,
            TestContext.Current.CancellationToken);

        var added = await context.AddToDownloadAsync(
            CreateSelection(_directory, requestedContent),
            preparedDownload,
            isAll: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, added);
        var downloads = context.ListState.Downloading.OrderBy(item => item.Order).ToArray();
        Assert.Equal(Path.Combine(_directory, "bangumi", firstPage.Name), downloads[0].DownloadBase.FilePath);
        Assert.Equal(Path.Combine(_directory, "bangumi", secondPage.Name), downloads[1].DownloadBase.FilePath);
        Assert.Equal("https://example.test/series.jpg", downloads[0].DownloadBase.CoverUrl);
        Assert.Equal("https://example.test/episode-1.jpg", downloads[0].DownloadBase.PageCoverUrl);
        Assert.Equal(string.Empty, downloads[1].DownloadBase.CoverUrl);
        Assert.Equal("https://example.test/episode-2.jpg", downloads[1].DownloadBase.PageCoverUrl);
        Assert.All(downloads, item => Assert.Equal(
            PlayStreamType.Bangumi,
            item.Downloading.PlayStreamType));
    }

    [Fact]
    public async Task IndependentAliasedSubtitleDownloadKeepsTheTemplateDirectoryAndRenamesTheFile()
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

        Assert.Equal(1, await context.AddToDownloadAsync(
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
        var subtitle = Assert.Single(context.ListState.Downloading, item => item.DownloadBase.NeedDownloadContent.Subtitle);
        Assert.Equal(
            mediaItem.DownloadBase.FilePath + "(1)",
            subtitle.DownloadBase.FilePath);
        Assert.Equal(2, context.ListState.Downloading.Count);
        Assert.Empty(context.Dialogs.Requests);
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
        var preparedDownload = await context.PrepareAsync(
            DownloadContentSelection.None with { Video = true },
            page);
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
        var preparedDownload = await context.PrepareAsync(
            DownloadContentSelection.None with { Video = true },
            page);
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
        var preparedPage = Assert.Single(Assert.Single(preparedDownload.Sections).Pages);
        Assert.Same(video, preparedPage.Video);
        Assert.Same(page, preparedPage.Page);
        Assert.Equal(
            new DownloadMediaCapabilities(DownloadMediaOutputModes.VideoOnly),
            preparedPage.AvailableMedia);
        Assert.True(page.IsSelected);
        Assert.Equal(1, infoService.StreamRequestCount);
    }

    [Fact]
    public async Task PreparationUsesRequestedMediaAndSettingsQualityForSupplementation()
    {
        using var context = CreateContext(generateMetadata: false);
        context.SetPreferredVideoQuality(112);
        var page = CreatePage(_ => Task.FromResult<IReadOnlyList<string>>([]));
        page.PlaybackAvailability = new PlayUrlAvailability(
        [
            new PlayUrlVideoAvailability(
                64,
                12,
                PlayUrlStreamKind.Dash,
                "720P 高清")
        ],
        []);
        page.VideoQuality = new VideoQuality
        {
            Quality = 64,
            QualityFormat = "720P 高清",
            SelectedVideoCodec = "H.265/HEVC"
        };
        page.AudioQualityFormat = string.Empty;
        var infoService = new PreparationInfoService(
            new VideoInfoView { Title = "settings-selection" },
            [new VideoSection { VideoPages = [page] }],
            new PlayUrl
            {
                Dash = new PlayUrlDash
                {
                    Video =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 64,
                            CodecId = 12,
                            BaseAddress = "https://media.invalid/video-64-hevc.m4s"
                        }
                    ],
                    Audio =
                    [
                        new PlayUrlDashVideo
                        {
                            Id = 30280,
                            BaseAddress = "https://media.invalid/audio-30280.m4s"
                        }
                    ]
                },
                SupportFormats =
                [
                    new PlayUrlSupportFormat
                    {
                        Quality = 64,
                        NewDescription = "720P 高清"
                    }
                ]
            });

        var prepared = await context.Service.PrepareAsync(
            infoService,
            DownloadContentSelection.None with { Video = true, Audio = true },
            TestContext.Current.CancellationToken);

        Assert.NotNull(prepared);
        var request = Assert.IsType<PlaybackDiscoveryRequest>(infoService.LastDiscoveryRequest);
        Assert.Equal(112, request.PreferredVideoQuality);
        Assert.Equal(30280, request.PreferredAudioQuality);
        Assert.True(request.RequireVideo);
        Assert.True(request.RequireAudio);
        Assert.Equal(64, page.VideoQuality!.Quality);
        Assert.Equal(PlaybackQualityMatchKind.Lower, page.VideoQualityMatch!.Kind);
        Assert.Equal("H.265/HEVC", page.VideoQuality.SelectedVideoCodec);
        Assert.Equal("高质量", page.AudioQualityFormat);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UgcDownloadsKeepEachVideosMetadataAndSelectedPages(bool selectEntry, bool hasPlayback)
    {
        var entry = CreateUgcDownloadVideo("BV17x411w7KC", 42, 1_700_000_000, 84);
        var multipart = CreateUgcDownloadVideo("BV1xx411c7mD", 43, 1_710_000_000, 85, 86, 87);
        var other = CreateUgcDownloadVideo("BV1GJ411x7h7", 44, 1_720_000_000, 88);
        var unselected = CreateUgcDownloadVideo("BV1Q541167Qg", 45, 1_730_000_000, 89);
        var videos = new[] { entry, multipart, other, unselected }.ToDictionary(video => video.Bvid);
        var detailRequests = new List<string>();
        var playbackRequests = new List<long>();
        var playback = new PlayUrl
        {
            Quality = 80,
            VideoCodecid = 7,
            Durl = [new PlayUrlDurl { SourceAddress = "https://media.invalid/video.mp4" }]
        };
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = new Uri(request.RequestAddress);
                if (uri.Host == "www.bilibili.com")
                {
                    return Task.FromResult("<html></html>");
                }

                if (uri.AbsolutePath == "/x/web-interface/wbi/view")
                {
                    var bvid = HttpUtility.ParseQueryString(uri.Query)["bvid"]!;
                    detailRequests.Add(bvid);
                    return Task.FromResult(JsonConvert.SerializeObject(new { code = 0, data = videos[bvid] }));
                }

                if (uri.AbsolutePath == "/x/player/wbi/playurl")
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    var cid = long.Parse(query["cid"]!, CultureInfo.InvariantCulture);
                    Assert.Contains(videos[query["bvid"]!].Pages!, page => page.Cid == cid);
                    playbackRequests.Add(cid);
                    return Task.FromResult(JsonConvert.SerializeObject(new { code = 0, data = playback }));
                }

                Assert.Equal("/x/web-interface/view/detail/tag", uri.AbsolutePath);
                return Task.FromResult("{\"code\":0,\"data\":[]}");
            }
        };
        using var context = CreateContext(generateMetadata: true, client: client);
        context.SetFileNameTemplate(
            FileNamePart.Section, FileNamePart.Slash,
            FileNamePart.MainTitle, FileNamePart.Slash,
            FileNamePart.PageTitle, FileNamePart.Slash,
            FileNamePart.VideoPublishTime);
        using var settings = new TestSettingsStore();
        entry.UgcSeason = new UgcSeason
        {
            Sections =
            [
                new UgcSection
                {
                    Id = 7,
                    Title = "collection-section",
                    Episodes = videos.Values.Select(video => new UgcEpisode
                    {
                        Aid = video.Aid,
                        Bvid = video.Bvid,
                        Cid = video.Pages![0].Cid,
                        Title = video == entry ? video.Title : $"listed-{video.Bvid}",
                        Page = video.Pages[0],
                        Pages = video.Pages,
                        Arc = new UgcArc
                        {
                            Pic = video.Pic,
                            Pubdate = video.Pubdate,
                            Ctime = video.Pubdate - 86_400
                        }
                    }).ToArray()
                }
            ]
        };
        var source = new VideoInfoService(
            entry, settings.Store, new VideoTagProvider(client), new TestWbiKeyProvider(), client);
        var videoInfo = source.GetVideoView(TestContext.Current.CancellationToken)!;
        var sections = source.GetVideoSections(cancellationToken: TestContext.Current.CancellationToken)!;
        foreach (var page in sections.SelectMany(section => section.VideoPages))
        {
            page.IsSelected = page.Bvid != unselected.Bvid && page.Cid != 85
                && (selectEntry || page.Bvid != entry.Bvid);
            if (hasPlayback)
            {
                SetPlayback(page, playback, isDurl: true);
            }
        }

        var requested = DownloadContentSelection.None with { Video = true, Cover = true };
        var prepared = await context.Service.PrepareAsync(
            videoInfo, sections, requested, isAll: false, TestContext.Current.CancellationToken);
        var result = await context.AddToDownloadResultAsync(
            CreateSelection(_directory, requested), prepared,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(entry.Bvid, videoInfo.Bvid);
        Assert.Equal(selectEntry ? 4 : 3, result.AddedCount);
        Assert.Equal(new[] { multipart.Bvid, other.Bvid }, detailRequests);
        long[] selectedCids = selectEntry ? [84, 86, 87, 88] : [86, 87, 88];
        Assert.Equal(hasPlayback ? [] : selectedCids, playbackRequests.Order());
        Assert.Equal(selectedCids, prepared.Sections.SelectMany(section => section.Pages).Select(page => page.Page.Cid).Order());
        Assert.Equal(selectedCids,
            context.ListState.Downloading.Select(item => item.DownloadBase.Cid).Order());
        foreach (var item in context.ListState.Downloading)
        {
            var expected = videos[item.DownloadBase.Bvid];
            var page = sections.SelectMany(section => section.VideoPages)
                .Single(candidate => candidate.Cid == item.DownloadBase.Cid);
            var section = sections.Single(candidate => candidate.VideoPages.Contains(page));
            var expectedPages = expected.Pages!;
            var expectedPage = expectedPages.Single(candidate => candidate.Cid == page.Cid);
            var expectedName = expectedPages.Count == 1 ? expected.Title : expectedPage.Part;
            var published = DateTimeOffset.FromUnixTimeSeconds(expected.Pubdate).LocalDateTime;
            Assert.Equal(expected.Title, item.DownloadBase.MainTitle);
            Assert.Equal(expectedName, item.DownloadBase.Name);
            Assert.Equal(expected.Pic, item.DownloadBase.CoverUrl);
            Assert.Equal(published, page.OriginalPublishTime);
            Assert.Equal(expected.Desc, item.Metadata!.Plot);
            Assert.Equal(published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.Metadata.Premiered);
            Assert.Equal(
                Path.Combine(_directory, section.Title, expected.Title, expectedName, page.PublishTime),
                item.DownloadBase.FilePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UgcDetailFailureDoesNotFallBackToEntryVideoMetadata(bool missingSelectedPage)
    {
        var detail = CreateUgcDownloadVideo("BV1GJ411x7h7", 43, 1_710_000_000, 85);
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(missingSelectedPage
                ? JsonConvert.SerializeObject(new { code = 0, data = detail })
                : "{\"code\":-404,\"message\":\"Not found\"}")
        };
        using var context = CreateContext(generateMetadata: false, client: client);
        var page = new VideoPage { Bvid = detail.Bvid, Cid = 86, IsSelected = true };

        await Assert.ThrowsAsync<DownKyi.Core.BiliApi.BilibiliApiResponseException>(() =>
            context.Service.PrepareAsync(
                new VideoInfoView { Bvid = "BV17x411w7KC", Title = "entry" },
                [new VideoSection { VideoPages = [page] }],
                DownloadContentSelection.None with { Cover = true },
                isAll: false,
                TestContext.Current.CancellationToken));

        Assert.Empty(context.ListState.Downloading);
        Assert.Empty(context.Queue.Enqueued);
    }

    [Fact]
    public async Task CancelingUgcDetailLookupCancelsPreparation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(true);
                return string.Empty;
            }
        };
        using var context = CreateContext(generateMetadata: false, client: client);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var preparation = context.Service.PrepareAsync(
            new VideoInfoView { Bvid = "BV17x411w7KC", Title = "entry" },
            [new VideoSection { VideoPages = [new VideoPage { Bvid = "BV1GJ411x7h7", Cid = 85, IsSelected = true }] }],
            DownloadContentSelection.None with { Cover = true },
            isAll: false,
            operation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await operation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.Empty(context.ListState.Downloading);
        Assert.Empty(context.Queue.Enqueued);
    }

    private static VideoView CreateUgcDownloadVideo(string bvid, long avid, long pubdate, params long[] cids) => new()
    {
        Aid = avid,
        Bvid = bvid,
        Title = $"title-{bvid}",
        Pic = $"https://images.invalid/{bvid}.jpg",
        Desc = $"description-{bvid}",
        Pubdate = pubdate,
        Pages = cids.Select((cid, index) => new CoreVideoPage
        {
            Cid = cid,
            Page = index + 1,
            Part = $"part-{index + 1}",
            FirstFrame = $"https://images.invalid/{cid}.jpg"
        }).ToArray()
    };

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

    private static DownloadHistoryRecord CreateHistory(
        VideoPage page,
        DownloadContentSelection requestedContent) =>
        new(
            new DownloadTaskId(Guid.NewGuid().ToString("N")),
            page.Cid,
            zoneId: 0,
            page.Order,
            mainTitle: "video",
            page.Name,
            durationText: string.Empty,
            videoCodecName: "H.264/AVC",
            resolutionId: 80,
            resolutionName: "1080P",
            audioCodecName: string.Empty,
            fileSizeText: null,
            publishedArtifacts: [],
            finishedTimestamp: 1,
            finishedTimeText: string.Empty,
            maximumSpeedText: null,
            requestedContent);

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
        private readonly DownKyi.Infrastructure.Settings.SettingsStore _settings;
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
            _settings = new DownKyi.Infrastructure.Settings.SettingsStore(settingsPath);
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    Quality = 80,
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
            var stateWriter = new DownloadTaskStateWriter(_taskService);
            var queue = taskQueue ?? Queue;
            _admission = new DownloadTaskAdmissionService(
                ListState,
                _taskService,
                _taskService,
                historyService,
                _projectionStore,
                new DownloadCommittedTaskDispatcher(queue, stateWriter),
                runtimeAvailability ?? new ReadyDownloadRuntimeAvailability(),
                physicalOutputPathResolver);
            Service = new AddToDownloadService(
                DownKyi.Core.BiliApi.VideoStream.PlayStreamType.Video,
                _admission,
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

        public void SetPreferredVideoQuality(int quality)
        {
            _settings.Update(settings => settings with
            {
                Video = settings.Video with { Quality = quality }
            });
        }

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

        public void UseLegacyDefaultDirectory(string directory)
        {
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    IsUseSaveVideoRootPath = AllowStatus.Yes,
                    SaveVideoRootPath = directory
                }
            });
        }

        public void SetFileNameTemplate(params FileNamePart[] parts)
        {
            _settings.Update(settings => settings with
            {
                Video = settings.Video with
                {
                    FileNameParts = [.. parts]
                }
            });
        }

        public async Task<int> AddToDownloadAsync(
            DownloadAddSelection selection,
            PreparedDownload preparedDownload,
            bool isAll = false,
            CancellationToken cancellationToken = default)
        {
            var result = await AddToDownloadResultAsync(
                selection,
                preparedDownload,
                isAll,
                cancellationToken).ConfigureAwait(true);
            return result.AddedCount;
        }

        public async Task<DownloadAddResult> AddToDownloadResultAsync(
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
                .AddToDownloadBatch(selection, [finalizedDownload], cancellationToken)
                .ConfigureAwait(true);
            return result;
        }

        public Task<PreparedDownload> PrepareAsync(params VideoPage[] pages)
        {
            return PrepareAsync(DownloadContentSelection.All, pages);
        }

        public Task<PreparedDownload> PrepareAsync(
            DownloadContentSelection requestedContent,
            params VideoPage[] pages)
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
                requestedContent,
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

        public PlaybackDiscoveryRequest? LastDiscoveryRequest { get; private set; }

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
            PlaybackDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastDiscoveryRequest = request;
            StreamRequestCount++;
            return Task.FromResult<PlayUrl?>(playUrl);
        }
    }

    private sealed class RecordingDownloadTaskStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        private readonly List<DownloadTask> _tasks = [];
        private readonly List<DownloadHistoryRecord> _history = [];

        public int AddCount { get; private set; }

        public int HistoryPageRequestCount { get; private set; }

        public int HistoryByCidRequestCount { get; private set; }

        public int HistoryByCidsRequestCount { get; private set; }

        public int UnfinishedByCidRequestCount { get; private set; }

        public int UnfinishedByCidsRequestCount { get; private set; }

        public Func<Task>? AfterAddAsync { get; set; }

        public int? ExceptionOnAddCall { get; set; }

        public async Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCount++;
            if (AddCount == ExceptionOnAddCall)
            {
                throw new IOException("Download task store failed.");
            }

            _tasks.Add(task);
            if (AfterAddAsync != null)
            {
                await AfterAddAsync().ConfigureAwait(false);
            }

            return OperationResult.Success();
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _history.Add(history);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _history.Clear();
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _tasks.RemoveAll(task => task.Id == taskId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _history.RemoveAll(record => record.Id == taskId);
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_tasks.SingleOrDefault(task => task.Id == taskId));
        }

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_tasks.Select(task =>
                DownloadOutputPathKey.Create(task.Output.BasePath, ignoreCase)).ToArray());

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            HistoryPageRequestCount++;
            return Task.FromResult(new DownloadHistoryPage(_history.ToArray(), null));
        }

        public Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidAsync(
            long cid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryByCidRequestCount++;
            return Task.FromResult<IReadOnlyList<DownloadHistoryRecord>>(
                _history.Where(record => record.Cid == cid).ToArray());
        }

        public Task<IReadOnlyList<DownloadHistoryRecord>> GetHistoryByCidsAsync(
            IReadOnlyCollection<long> cids,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoryByCidsRequestCount++;
            var requestedCids = cids.ToHashSet();
            return Task.FromResult<IReadOnlyList<DownloadHistoryRecord>>(
                _history.Where(record => requestedCids.Contains(record.Cid)).ToArray());
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DownloadTask>>(_tasks.ToArray());
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidAsync(
            long cid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnfinishedByCidRequestCount++;
            return Task.FromResult<IReadOnlyList<DownloadTask>>(
                _tasks.Where(task => task.Metadata.Media.Cid == cid).ToArray());
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedByCidsAsync(
            IReadOnlyCollection<long> cids,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnfinishedByCidsRequestCount++;
            var requestedCids = cids.ToHashSet();
            return Task.FromResult<IReadOnlyList<DownloadTask>>(
                _tasks.Where(task => requestedCids.Contains(task.Metadata.Media.Cid)).ToArray());
        }

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
            var index = _tasks.FindIndex(candidate => candidate.Id == task.Id);
            if (index >= 0)
            {
                _tasks[index] = task;
            }

            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _tasks.RemoveAll(candidate => candidate.Id == task.Id);
            _history.Add(history);
            return Task.FromResult(OperationResult.Success());
        }

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
