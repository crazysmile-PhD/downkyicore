using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Bilibili.Community.Service.Dm.V1;
using DownKyi.Application.Bilibili;
using DownKyi.Application.Downloads;
using DownKyi.Application.Settings;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Bilibili;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadArtifactStageTests
{
    [Theory]
    [InlineData(DanmakuOutputFormat.Ass, true, false)]
    [InlineData(DanmakuOutputFormat.Xml, false, true)]
    public async Task SingleDanmakuOutputFormatsCreateOnlyTheSelectedFile(
        DanmakuOutputFormat outputFormat,
        bool expectsAss,
        bool expectsXml)
    {
        const string content = "字幕 <>&\"'";
        var client = CreateDanmakuClient(
        [
            new DanmakuElem
            {
                Id = 1,
                Progress = 1_000,
                Mode = 1,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "synthetic",
                Content = content
            }
        ]);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            danmaku: true,
            danmakuOutputFormat: outputFormat).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(expectsAss, File.Exists($"{context.Downloading.DownloadBase!.FilePath}.ass"));
        Assert.Equal(expectsXml, File.Exists($"{context.Downloading.DownloadBase.FilePath}.xml"));
        if (expectsAss)
        {
            var bytes = await File.ReadAllBytesAsync(
                $"{context.Downloading.DownloadBase.FilePath}.ass", TestContext.Current.CancellationToken);
            Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.Contains(content, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        if (expectsXml)
        {
            var text = await File.ReadAllTextAsync(
                $"{context.Downloading.DownloadBase.FilePath}.xml", TestContext.Current.CancellationToken);
            var document = XDocument.Parse(text);
            Assert.Equal("utf-8", document.Declaration?.Encoding);
            Assert.Equal(content, Assert.Single(document.Root!.Elements("d")).Value);
        }
    }

    [Fact]
    public async Task AssAndXmlShareOneSourceSnapshotAndHaveDistinctDurableOwners()
    {
        var requests = new List<string>();
        var client = CreateDanmakuClient(
        [
            new DanmakuElem
            {
                Id = 2,
                Progress = 2_000,
                Mode = 1,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "synthetic",
                Content = "shared snapshot"
            }
        ], requests);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            danmaku: true,
            danmakuOutputFormat: DanmakuOutputFormat.AssAndXml).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        Assert.Single(requests, request =>
            request.Contains("/x/v2/dm/web/view", StringComparison.Ordinal));
        Assert.Single(requests, request =>
            request.Contains("/x/v2/dm/web/seg.so", StringComparison.Ordinal));
        Assert.True(File.Exists($"{context.Downloading.DownloadBase!.FilePath}.ass"));
        Assert.True(File.Exists($"{context.Downloading.DownloadBase.FilePath}.xml"));
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(
            $"{context.Downloading.DownloadBase.FilePath}.ass",
            task.Plan.TransferFiles[DownloadArtifactWriter.DanmakuAssTransferKey]);
        Assert.Equal(
            $"{context.Downloading.DownloadBase.FilePath}.xml",
            task.Plan.TransferFiles[DownloadArtifactWriter.DanmakuXmlTransferKey]);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
    }

    [Fact]
    public async Task AssAndXmlShareExplicitlyFilteredSnapshotWhileXmlKeepsOriginalSourceData()
    {
        const long blockedSenderUid = 123456;
        var blockedSenderHash = DanmakuSender.GetMidHash(blockedSenderUid);
        var elements = new[]
        {
            new DanmakuElem
            {
                Id = 1,
                Progress = 1_000,
                Mode = 5,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "top",
                Content = "blocked-mode"
            },
            new DanmakuElem
            {
                Id = 2,
                Progress = 2_000,
                Mode = 1,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "keyword",
                Content = "前方劇透"
            },
            new DanmakuElem
            {
                Id = 3,
                Progress = 3_000,
                Mode = 1,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = blockedSenderHash,
                Content = "blocked-sender"
            },
            new DanmakuElem
            {
                Id = 4,
                Progress = 4_321,
                Mode = 1,
                Fontsize = 30,
                Color = 16_711_680,
                Ctime = 1_700_000_000,
                Pool = 1,
                MidHash = "kept-source",
                Content = "保留😂\n<>&\"'"
            },
            new DanmakuElem
            {
                Id = 5,
                Progress = 5_000,
                Mode = 1,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "emoji-only",
                Content = "😂"
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            CreateDanmakuClient(elements),
            danmaku: true,
            danmakuOutputFormat: DanmakuOutputFormat.AssAndXml,
            configureDanmaku: settings => settings with
            {
                TopFilter = AllowStatus.Yes,
                RemoveEmojiAndSpecialCharacters = AllowStatus.Yes,
                BlockedKeywords = ["劇透"],
                BlockedSenderUids = [blockedSenderUid]
            }).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var outputBasePath = context.Downloading.DownloadBase!.FilePath;
        var document = XDocument.Load($"{outputBasePath}.xml");
        var xmlDanmakus = document.Root!.Elements("d").ToArray();
        Assert.Equal(2, xmlDanmakus.Length);
        var kept = Assert.Single(xmlDanmakus, element => element.Value.StartsWith("保留", StringComparison.Ordinal));
        Assert.Equal("保留😂\n<>&\"'", kept.Value);
        Assert.Equal(
            "4.321,1,30,16711680,1700000000,1,kept-source,4",
            kept.Attribute("p")?.Value);
        Assert.Contains(xmlDanmakus, element => element.Value == "😂");
        Assert.DoesNotContain(xmlDanmakus, element => element.Value == "blocked-mode");
        Assert.DoesNotContain(xmlDanmakus, element => element.Value == "前方劇透");
        Assert.DoesNotContain(xmlDanmakus, element => element.Value == "blocked-sender");

        var ass = await File.ReadAllTextAsync(
            $"{outputBasePath}.ass",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Contains("保留", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("😂", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked-mode", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("前方劇透", ass, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked-sender", ass, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XmlKeepsUnsupportedAndCrowdedSourceDanmakus()
    {
        var elements = Enumerable.Range(1, 30)
            .Select(index => new DanmakuElem
            {
                Id = index,
                Progress = 1_000,
                Mode = 5,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "synthetic",
                Content = $"crowded-{index}"
            })
            .Prepend(new DanmakuElem
            {
                Id = 100,
                Progress = 500,
                Mode = 7,
                Fontsize = 25,
                Color = 0xFFFFFF,
                MidHash = "synthetic",
                Content = "unsupported-mode"
            })
            .ToArray();
        using var context = await ArtifactTestContext.CreateAsync(
            CreateDanmakuClient(elements),
            danmaku: true,
            danmakuOutputFormat: DanmakuOutputFormat.Xml).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var document = XDocument.Load($"{context.Downloading.DownloadBase!.FilePath}.xml");
        var danmakus = document.Root!.Elements("d").ToArray();
        Assert.Equal(elements.Length, danmakus.Length);
        Assert.Contains(danmakus, element => element.Value == "unsupported-mode");
        Assert.Equal(30, danmakus.Count(element => element.Value.StartsWith(
            "crowded-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(DanmakuOutputFormat.Ass, "ass", DownloadArtifactWriter.DanmakuAssTransferKey)]
    [InlineData(DanmakuOutputFormat.Xml, "xml", DownloadArtifactWriter.DanmakuXmlTransferKey)]
    public async Task DanmakuWriteFailureIsTypedAndTheClaimRemainsDurable(
        DanmakuOutputFormat outputFormat, string extension, string transferKey)
    {
        using var context = await ArtifactTestContext.CreateAsync(
            CreateDanmakuClient([]),
            danmaku: true,
            useMissingOutputDirectory: true,
            danmakuOutputFormat: outputFormat).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.io", result.Error?.Code);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(
            $"{context.Downloading.DownloadBase!.FilePath}.{extension}",
            task.Plan.TransferFiles[transferKey]);
        Assert.Empty(context.GetPhysicalArtifactFiles());
    }

    [Theory]
    [InlineData(DanmakuOutputFormat.Ass, "ass", DownloadArtifactWriter.DanmakuAssTransferKey)]
    [InlineData(DanmakuOutputFormat.Xml, "xml", DownloadArtifactWriter.DanmakuXmlTransferKey)]
    public async Task DanmakuCancellationRemainsVisibleAfterDurableClaim(
        DanmakuOutputFormat outputFormat, string extension, string transferKey)
    {
        using var cancellation = new CancellationTokenSource();
        var client = CreateDanmakuClient(
            [],
            segmentStarted: () => cancellation.Cancel());
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            danmaku: true,
            danmakuOutputFormat: outputFormat).ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Stage.ExecuteAsync(context.Execution, cancellation.Token)).ConfigureAwait(true);

        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(
            $"{context.Downloading.DownloadBase!.FilePath}.{extension}",
            task.Plan.TransferFiles[transferKey]);
    }

    [Fact]
    public async Task AssAndXmlCancellationPreservesTheCombinedDurablePlan()
    {
        using var cancellation = new CancellationTokenSource();
        var client = CreateDanmakuClient(
            [],
            segmentStarted: () => cancellation.Cancel());
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            danmaku: true,
            danmakuOutputFormat: DanmakuOutputFormat.AssAndXml).ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Stage.ExecuteAsync(context.Execution, cancellation.Token)).ConfigureAwait(true);

        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Contains(DownloadArtifactWriter.DanmakuAssTransferKey, task.Plan.TransferFiles);
        Assert.Contains(DownloadArtifactWriter.DanmakuXmlTransferKey, task.Plan.TransferFiles);
        var currentSettings = context.SetDanmakuOutputFormat(DanmakuOutputFormat.Ass);
        var resumedInput = DownloadExecutionContextFactory.CreateInput(task, currentSettings);
        Assert.Equal(DanmakuOutputFormat.AssAndXml, resumedInput.DanmakuSettings.OutputFormat);
    }

    [Fact]
    public async Task MalformedOwnedStagedXmlIsRegeneratedBeforePublishing()
    {
        var requests = new List<string>();
        using var context = await ArtifactTestContext.CreateAsync(
            CreateDanmakuClient(
            [
                new DanmakuElem
                {
                    Id = 3,
                    Progress = 3_000,
                    Mode = 1,
                    Fontsize = 25,
                    Color = 0xFFFFFF,
                    MidHash = "synthetic",
                    Content = "regenerated"
                }
            ],
            requests),
            danmaku: true,
            danmakuOutputFormat: DanmakuOutputFormat.Xml).ConfigureAwait(true);
        var xmlFile = $"{context.Downloading.DownloadBase!.FilePath}.xml";
        await context.ClaimTransferFileAsync(
            DownloadArtifactWriter.DanmakuXmlTransferKey,
            xmlFile).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            xmlFile,
            "<i><d p=\"incomplete\"",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, requests.Count);
        var document = XDocument.Load(xmlFile);
        Assert.Equal("i", document.Root?.Name.LocalName);
        Assert.Equal("regenerated", Assert.Single(document.Root!.Elements("d")).Value);
    }

    [Fact]
    public async Task RecordedDanmakuDoesNotCompleteRetryAfterPublishedFileDisappears()
    {
        var directory = Path.Combine(Path.GetTempPath(), "downkyi-artifact-retry",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new DownKyi.Infrastructure.Settings.SettingsStore(
                Path.Combine(directory, "settings.json"));
            var downloadBase = new DownloadBase
            {
                Id = "missing-published-danmaku",
                FilePath = Path.Combine(directory, "output"),
                NeedDownloadContent = DownloadContentSelection.None with { Danmaku = true }
            };
            var item = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = downloadBase.Id,
                    DownloadBase = downloadBase,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            var context = DownloadExecutionContextTestFactory.Create(item, settings.Current);
            var published = Path.Combine(directory, "output.xml");
            context.PublishedArtifacts["danmaku"] = published;
            await File.WriteAllBytesAsync(published, [1], TestContext.Current.CancellationToken);
            var stage = new ValidateStage();

            var beforeRemoval = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);
            Assert.True(beforeRemoval.IsSuccess);

            File.Delete(published);
            var afterRemoval = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);
            Assert.False(afterRemoval.IsSuccess);
            Assert.Equal("download.validate.danmaku", afterRemoval.Error?.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReopenedTaskGeneratesPersistedNfoAfterCurrentSettingIsDisabled()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-reopened-nfo-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "download.db");
        var settingsPath = Path.Combine(directory, "settings.json");
        var taskId = new DownloadTaskId("persisted-nfo");
        try
        {
            using var settings = new DownKyi.Infrastructure.Settings.SettingsStore(settingsPath);
            settings.Update(current => current with
            {
                Video = current.Video with
                {
                    Content = current.Video.Content with { GenerateMovieMetadata = true }
                }
            });
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                FilePath = Path.Combine(directory, "persisted-output")
            };
            downloadBase.NeedDownloadContent = DownloadContentSelection.None;

            var admitted = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = taskId.Value,
                    DownloadBase = downloadBase,
                    DownloadStatus = DownloadStatus.WaitForDownload
                },
                Metadata = new MovieMetadata
                {
                    Title = "Persisted title",
                    Plot = "Persisted plot",
                    Year = "2026",
                    Premiered = "2026-09-09",
                    BilibiliId = new UniqueId("bilibili", "BV1PERSISTED")
                }
            };
            admitted.Metadata.Genres.Add("Persisted genre");

            using (var firstStore = CreateSqliteStore(databasePath))
            {
                await firstStore.InitializeAsync(TestContext.Current.CancellationToken);
                var firstHistoryService = DownloadHistoryService.CreateForSharedStore(firstStore);
                using var firstTasks = new DownloadTaskApplicationService(
                    firstStore,
                    firstHistoryService,
                    new SystemClock());
                using var firstProjections = new DownloadTaskProjectionStore(
                    firstTasks,
                    firstHistoryService,
                    new SystemClock());
                await firstProjections.AddDownloadingAsync(
                    admitted,
                    TestContext.Current.CancellationToken);
                var firstWriter = new DownloadTaskStateWriter(firstTasks);
                await firstWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
                await firstWriter.PauseAsync(taskId, TestContext.Current.CancellationToken);
                await firstWriter.ConfirmPausedAsync(taskId, TestContext.Current.CancellationToken);
            }

            settings.Update(current => current with
            {
                Video = current.Video with
                {
                    Content = current.Video.Content with { GenerateMovieMetadata = false }
                }
            });
            using var reopenedStore = CreateSqliteStore(databasePath);
            await reopenedStore.InitializeAsync(TestContext.Current.CancellationToken);
            var reopenedHistoryService = DownloadHistoryService.CreateForSharedStore(reopenedStore);
            using var reopenedTasks = new DownloadTaskApplicationService(
                reopenedStore,
                reopenedHistoryService,
                new SystemClock());
            using var reopenedProjections = new DownloadTaskProjectionStore(
                reopenedTasks,
                reopenedHistoryService,
                new SystemClock());
            var startup = await reopenedProjections.GetDownloadingStateAsync(
                TestContext.Current.CancellationToken);
            var reopenedItem = Assert.Single(startup.Projections);
            var stateWriter = new DownloadTaskStateWriter(reopenedTasks);
            await stateWriter.ResumeAsync(taskId, TestContext.Current.CancellationToken);
            var execution = DownloadExecutionContextTestFactory.Create(
                reopenedItem,
                settings.Current);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
            var stage = new DownloadArtifactsStage(
                new DownloadArtifactWriter(
                    new TestWbiKeyProvider(),
                    stateWriter,
                    NullLogger<DownloadArtifactWriter>.Instance,
                    new TestBilibiliApiClient()),
                new DownloadActivityPresenter(reopenedProjections, stateWriter));

            var result = await stage.ExecuteAsync(execution, TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.False(settings.Current.Video.Content.GenerateMovieMetadata);
            var nfo = await File.ReadAllTextAsync(
                $"{downloadBase.FilePath}.nfo",
                TestContext.Current.CancellationToken);
            Assert.Contains("Persisted title", nfo, StringComparison.Ordinal);
            Assert.Contains("Persisted genre", nfo, StringComparison.Ordinal);
        }
        finally
        {
            ClearSqlitePool(databasePath);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CoverHttpFailureStopsBeforeFinalize()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                Task.FromException(new HttpRequestException("unavailable"))
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/cover.jpg").ConfigureAwait(true);
        var finalized = false;

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new CallbackStage(() => finalized = true)],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(run.Result.IsSuccess);
        Assert.Equal("download.artifact.cover.http", run.Result.Error?.Code);
        Assert.False(finalized);
    }

    [Fact]
    public async Task CoverUrlsUseHttpsThroughProductionDownloadPath()
    {
        using var productionClient = ProductionBilibiliClientContext.Create();
        using var context = await ArtifactTestContext.CreateAsync(
            productionClient.Client,
            cover: true,
            coverUrl: "http://i0.hdslb.com/bfs/archive/main%2Fcover.jpg?token=%2f%2B",
            pageCoverUrl: "//i1.hdslb.com/bfs/archive/page%2Fcover.jpg?token=%2B%2f")
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "https://i0.hdslb.com/bfs/archive/main%2Fcover.jpg?token=%2f%2B",
            Assert.Single(productionClient.Requests).Address.OriginalString);
        Assert.All(
            productionClient.Requests,
            request =>
            {
                Assert.Equal(Uri.UriSchemeHttps, request.Address.Scheme);
                Assert.False(request.HasCookie);
                Assert.False(request.HasOrigin);
            });
        Assert.Equal(0, productionClient.CookieReads);
        Assert.Equal(0, productionClient.BuvidReads);
    }

    [Theory]
    [InlineData("file://server/share/cover.jpg")]
    [InlineData("ftp://i0.hdslb.com/cover.jpg")]
    [InlineData("data:image/png;base64,AA==")]
    public async Task UnsupportedCoverUrlStopsBeforeProductionHttpClient(string address)
    {
        using var productionClient = ProductionBilibiliClientContext.Create();
        using var context = await ArtifactTestContext.CreateAsync(
            productionClient.Client,
            cover: true,
            coverUrl: address).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.cover.http", result.Error?.Code);
        Assert.Empty(productionClient.Requests);
    }

    [Fact]
    public async Task ZeroByteCoverFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, _) =>
            {
                using var output = File.Create(destination);
                return Task.CompletedTask;
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/cover.bin").ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.cover.invalid", result.Error?.Code);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        Assert.Single(context.GetPhysicalArtifactFiles());
    }

    [Theory]
    [InlineData(nameof(CoverFailureMode.HtmlError))]
    [InlineData(nameof(CoverFailureMode.HttpBeforeWrite))]
    [InlineData(nameof(CoverFailureMode.IoAfterWrite))]
    [InlineData(nameof(CoverFailureMode.PermissionAfterWrite))]
    [InlineData(nameof(CoverFailureMode.CancellationAfterWrite))]
    public async Task CoverFailureStateSpaceEndsWithEveryPhysicalFileDurablyOwned(
        string failureModeName)
    {
        var failureMode = Enum.Parse<CoverFailureMode>(failureModeName);
        var client = CreateCoverFailureClient(failureMode);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/cover.bin").ConfigureAwait(true);

        if (failureMode == CoverFailureMode.CancellationAfterWrite)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                context.Stage.ExecuteAsync(
                    context.Execution,
                    TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }
        else
        {
            var result = await context.Stage.ExecuteAsync(
                context.Execution,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.False(result.IsSuccess);
        }

        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        if (failureMode == CoverFailureMode.HttpBeforeWrite)
        {
            Assert.Empty(context.GetPhysicalArtifactFiles());
        }
        else
        {
            Assert.Single(context.GetPhysicalArtifactFiles());
        }
    }

    [Theory]
    [InlineData(nameof(ArtifactKind.MainCover))]
    [InlineData(nameof(ArtifactKind.PageCover))]
    [InlineData(nameof(ArtifactKind.Subtitle))]
    [InlineData(nameof(ArtifactKind.Danmaku))]
    [InlineData(nameof(ArtifactKind.Nfo))]
    public async Task FileProducingArtifactStateSpaceOwnsEveryCreatedOutput(string kindName)
    {
        var kind = Enum.Parse<ArtifactKind>(kindName);
        var client = CreateSuccessfulArtifactClient(kind);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: kind is ArtifactKind.MainCover or ArtifactKind.PageCover,
            subtitle: kind == ArtifactKind.Subtitle,
            danmaku: kind == ArtifactKind.Danmaku,
            generateMetadata: kind == ArtifactKind.Nfo,
            coverUrl: kind == ArtifactKind.MainCover ? "https://example.test/main.bin" : null,
            pageCoverUrl: kind == ArtifactKind.PageCover ? "https://example.test/page.bin" : null)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        Assert.NotEmpty(context.GetPhysicalArtifactFiles());
        if (kind == ArtifactKind.Subtitle)
        {
            Assert.Contains(DownloadArtifactWriter.DefaultSubtitleTransferKey, task.Plan.TransferFiles.Keys);
            Assert.Contains(DownloadArtifactWriter.GetSubtitleTrackTransferKey(11), task.Plan.TransferFiles.Keys);
            Assert.Contains(DownloadArtifactWriter.GetSubtitleTrackTransferKey(22), task.Plan.TransferFiles.Keys);
            Assert.Equal(3, context.GetPhysicalArtifactFiles().Length);
        }
    }

    [Theory]
    [InlineData(nameof(ArtifactKind.MainCover), nameof(DownloadActionKind.Cover))]
    [InlineData(nameof(ArtifactKind.Subtitle), nameof(DownloadActionKind.Subtitle))]
    [InlineData(nameof(ArtifactKind.Danmaku), nameof(DownloadActionKind.Danmaku))]
    public async Task IndependentOnlyActionProducesAValidatedSuccessfulOutput(
        string kindName,
        string actionName)
    {
        var kind = Enum.Parse<ArtifactKind>(kindName);
        var action = Enum.Parse<DownloadActionKind>(actionName);
        using var context = await ArtifactTestContext.CreateAsync(
            CreateSuccessfulArtifactClient(kind),
            cover: kind == ArtifactKind.MainCover,
            subtitle: kind == ArtifactKind.Subtitle,
            danmaku: kind == ArtifactKind.Danmaku,
            coverUrl: kind == ArtifactKind.MainCover ? "https://example.test/main.bin" : null)
            .ConfigureAwait(true);

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new ValidateStage()],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(run.Result.IsSuccess, run.Result.Error?.Message);
        Assert.Equal(
            DownloadActionResultStatus.Succeeded,
            context.Execution.ActionResults.Results[action]);
    }

    [Fact]
    public async Task OwnershipOracleRejectsSyntheticMissingOwnerMutation()
    {
        var client = CreateSuccessfulArtifactClient(ArtifactKind.MainCover);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.bin").ConfigureAwait(true);
        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(result.IsSuccess);

        var task = await context.GetTaskAsync().ConfigureAwait(true);
        var physicalFiles = context.GetPhysicalArtifactFiles();
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        var removedOwner = Assert.Single(physicalFiles);
        var mutatedOwners = task.Plan.TransferFiles.Values
            .Where(path => !PathComparer.Equals(Path.GetFullPath(path), removedOwner))
            .ToArray();

        var detectedOrphans = FindUnownedArtifactFiles(physicalFiles, mutatedOwners);

        Assert.True(
            PathComparer.Equals(removedOwner, Assert.Single(detectedOrphans)),
            "The ownership oracle did not report the intentionally orphaned file.");
    }

    [Theory]
    [InlineData(PlayStreamType.Video, "https://example.test/main.png", DownloadArtifactWriter.MainCoverTransferKey)]
    [InlineData(PlayStreamType.Cheese, "https://example.test/page.jpg", DownloadArtifactWriter.PageCoverTransferKey)]
    public async Task CoverSelectionCreatesOnlyThePreferredImage(
        PlayStreamType streamType,
        string expectedAddress,
        string expectedKey)
    {
        var requestedAddresses = new List<string>();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, destination, token) =>
            {
                requestedAddresses.Add(request.RequestAddress);
                return File.WriteAllTextAsync(destination, "image", token);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg",
            streamType: streamType).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(expectedAddress, Assert.Single(requestedAddresses));
        Assert.Equal(expectedKey, Assert.Single(task.Plan.TransferFiles).Key);
        Assert.Equal(
            Assert.Single(context.GetPhysicalArtifactFiles()),
            context.Execution.PreferredCoverFile);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
    }

    [Fact]
    public async Task BangumiEmbeddedVideoCoverOwnerCreatesEpisodeAndSeriesImages()
    {
        var requestedAddresses = new List<string>();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, destination, token) =>
            {
                requestedAddresses.Add(request.RequestAddress);
                return File.WriteAllTextAsync(destination, "image", token);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg",
            episodeId: -1,
            streamType: PlayStreamType.Bangumi).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["https://example.test/page.jpg", "https://example.test/main.png"],
            requestedAddresses);
        Assert.Equal(
            context.Downloading.DownloadBase.FilePath + ".jpg",
            context.Execution.PreferredCoverFile);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(
            [DownloadArtifactWriter.MainCoverTransferKey, DownloadArtifactWriter.PageCoverTransferKey],
            task.Plan.TransferFiles.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                context.Downloading.DownloadBase.FilePath + ".Cover.png",
                context.Downloading.DownloadBase.FilePath + ".jpg"
            ],
            context.GetPhysicalArtifactFiles());
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
    }

    [Theory]
    [InlineData(PlayStreamType.Video, "", "https://example.test/page.jpg")]
    [InlineData(PlayStreamType.Cheese, "https://example.test/main.png", "")]
    public async Task MissingPreferredCoverAddressUsesTheOtherImage(
        PlayStreamType streamType,
        string mainAddress,
        string pageAddress)
    {
        var requestedAddresses = new List<string>();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, destination, token) =>
            {
                requestedAddresses.Add(request.RequestAddress);
                return File.WriteAllTextAsync(destination, "image", token);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: mainAddress,
            pageCoverUrl: pageAddress,
            streamType: streamType).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            string.IsNullOrEmpty(mainAddress) ? pageAddress : mainAddress,
            Assert.Single(requestedAddresses));
        Assert.Single(context.GetPhysicalArtifactFiles());
    }

    [Theory]
    [InlineData(PlayStreamType.Video, "https://example.test/main.png", "https://example.test/page.jpg")]
    [InlineData(PlayStreamType.Cheese, "https://example.test/page.jpg", "https://example.test/main.png")]
    public async Task MissingPreferredCoverResponseUsesTheOtherImage(
        PlayStreamType streamType,
        string missingAddress,
        string fallbackAddress)
    {
        var requestedAddresses = new List<string>();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, destination, token) =>
            {
                requestedAddresses.Add(request.RequestAddress);
                return request.RequestAddress == missingAddress
                    ? Task.FromException(new HttpRequestException(
                        "not found", null, HttpStatusCode.NotFound))
                    : File.WriteAllTextAsync(destination, "fallback", token);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg",
            streamType: streamType).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([missingAddress, fallbackAddress], requestedAddresses);
        Assert.Equal("fallback", await File.ReadAllTextAsync(
            Assert.Single(context.GetPhysicalArtifactFiles()),
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        AssertPhysicalArtifactsAreDurablyOwned(context, await context.GetTaskAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task MissingBangumiSeriesCoverKeepsTheEpisodeCover()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, destination, token) =>
                request.RequestAddress.EndsWith("main.png", StringComparison.Ordinal)
                    ? Task.FromException(new HttpRequestException(
                        "not found", null, HttpStatusCode.NotFound))
                    : File.WriteAllTextAsync(destination, "episode", token)
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg",
            streamType: PlayStreamType.Bangumi).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("episode", await File.ReadAllTextAsync(
            Assert.Single(context.GetPhysicalArtifactFiles()),
            TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task TransientCoverFailureDoesNotSilentlyUseTheOtherImage()
    {
        var requestedAddresses = new List<string>();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (request, _, _) =>
            {
                requestedAddresses.Add(request.RequestAddress);
                throw new HttpRequestException("temporary failure", null, HttpStatusCode.ServiceUnavailable);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg").ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.cover.http-server-error", result.Error?.Code);
        Assert.Equal("https://example.test/main.png", Assert.Single(requestedAddresses));
    }

    [Fact]
    public async Task ExistingPageCoverIsRetainedWithoutDownloadingAnotherImage()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                throw new InvalidOperationException("Existing cover should be reused.")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg").ConfigureAwait(true);
        var existing = context.Downloading.DownloadBase!.FilePath + ".jpg";
        await File.WriteAllTextAsync(
            existing, "existing", TestContext.Current.CancellationToken).ConfigureAwait(true);
        context.Execution.PublishedArtifacts[DownloadArtifactWriter.PageCoverTransferKey] = existing;

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("existing", await File.ReadAllTextAsync(
            existing, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(existing, context.Execution.PreferredCoverFile);
        Assert.Single(context.GetPhysicalArtifactFiles());
    }

    [Fact]
    public async Task ExistingStagedPageAndMainCoversKeepBothDurableOwners()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                throw new InvalidOperationException("Existing covers should be reused.")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/main.png",
            pageCoverUrl: "https://example.test/page.jpg").ConfigureAwait(true);
        context.Execution.StagingDirectory = Path.GetDirectoryName(
            context.Downloading.DownloadBase!.FilePath);
        var pageFile = DownloadOutputLayout.GetPageCoverPath(
            context.Execution.WorkingBasePath, context.Execution.Input.Metadata.PageCoverAddress);
        var mainFile = DownloadOutputLayout.GetMainCoverPath(
            context.Execution.WorkingBasePath, context.Execution.Input.Metadata.CoverAddress);
        await context.ClaimTransferFileAsync(DownloadArtifactWriter.PageCoverTransferKey, pageFile)
            .ConfigureAwait(true);
        await context.ClaimTransferFileAsync(DownloadArtifactWriter.MainCoverTransferKey, mainFile)
            .ConfigureAwait(true);
        await File.WriteAllTextAsync(pageFile, "page", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await File.WriteAllTextAsync(mainFile, "main", TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, context.Execution.UnpublishedArtifacts.Count);
        AssertPhysicalArtifactsAreDurablyOwned(context, await context.GetTaskAsync().ConfigureAwait(true));
    }

    [Fact]
    public async Task CoverOnlyTaskWithNoAvailableImageFailsValidation()
    {
        using var context = await ArtifactTestContext.CreateAsync(
            new TestBilibiliApiClient(),
            cover: true).ConfigureAwait(true);

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new ValidateStage()],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(run.Result.IsSuccess);
        Assert.Equal("download.validate.no-resource", run.Result.Error?.Code);
        Assert.Empty(context.GetPhysicalArtifactFiles());
    }

    [Fact]
    public async Task SubtitlePathChangesPreserveEveryDurableOwnerAcrossRetries()
    {
        var metadataRequest = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) =>
            {
                if (request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal))
                {
                    var language = metadataRequest++ == 0 ? "Chinese" : "Traditional-Chinese";
                    var metadata = """
                        {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"SUBTITLE_LANGUAGE","subtitle_url":"//example.test/subtitle.json","type":0}]}}}
                        """.Replace("SUBTITLE_LANGUAGE", language, StringComparison.Ordinal);
                    return Task.FromResult(metadata);
                }

                return Task.FromResult(
                    """
                    {"body":[{"from":0,"to":1,"location":2,"content":"hello"}]}
                    """);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var first = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var second = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        Assert.Contains(
            $"{context.Downloading.DownloadBase.FilePath}_Chinese.srt",
            task.Plan.TransferFiles.Values);
        Assert.Contains(
            $"{context.Downloading.DownloadBase.FilePath}_Traditional-Chinese.srt",
            task.Plan.TransferFiles.Values);
        Assert.Equal(3, context.GetPhysicalArtifactFiles().Length);
    }

    [Fact]
    public async Task MissingSubtitleResourceIsAnExplicitSuccessfulSkip()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(
                """
                {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[]}}}
                """)
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(context.Execution.SubtitleFiles));
    }

    [Fact]
    public async Task UnmatchedAutomaticSubtitlePreferenceSkipsWithoutDownloadingAnotherLanguage()
    {
        var requests = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) =>
            {
                requests++;
                return Task.FromResult(
                    """
                    {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":22,"lan":"en","lan_doc":"English","subtitle_url":"//example.test/en.json","type":0}]}}}
                    """);
            }
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client, subtitle: true,
            preferredSubtitleLanguage: SubtitleLanguagePreference.ChineseSimplified).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, requests);
        Assert.Empty(context.GetPhysicalArtifactFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedSubtitleWithEmptyBodyFailsForManualAndAutomaticSelection(bool automatic)
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal)
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/zh.json","type":0}]}}}
                      """
                    : """{"body":[]}""")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client, subtitle: true,
            selectedSubtitleTrackIds: automatic ? null : [11],
            preferredSubtitleLanguage: automatic
                ? SubtitleLanguagePreference.ChineseSimplified
                : SubtitleLanguagePreference.Unspecified).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.missing-tracks", result.Error?.Code);
        Assert.Empty(context.GetPhysicalArtifactFiles());
    }

    [Fact]
    public async Task SelectedDefaultSubtitleIsCopiedToTheDefaultArtifact()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal)
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/zh.json","type":0},{"id":22,"lan":"en","lan_doc":"English","subtitle_url":"//example.test/en.json","type":1}]}}}
                      """
                    : request.RequestAddress.EndsWith("/en.json", StringComparison.Ordinal)
                        ? """{"body":[{"from":0,"to":1,"content":"english"}]}"""
                        : """{"body":[{"from":0,"to":1,"content":"chinese"}]}""")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            subtitle: true,
            selectedSubtitleTrackIds: [11, 22],
            defaultSubtitleTrackId: 22).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Contains("english", await File.ReadAllTextAsync(
            context.Downloading.DownloadBase.FilePath + ".srt", TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains("chinese", await File.ReadAllTextAsync(
            context.Downloading.DownloadBase.FilePath + "_Chinese.srt", TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedSubtitlesWithoutDefaultDoNotCreateDefaultArtifact()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal)
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/zh.json","type":0},{"id":22,"lan":"en","lan_doc":"English","subtitle_url":"//example.test/en.json","type":1}]}}}
                      """
                    : """{"body":[{"from":0,"to":1,"content":"subtitle"}]}""")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            subtitle: true,
            selectedSubtitleTrackIds: [11, 22]).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(File.Exists(context.Downloading.DownloadBase.FilePath + ".srt"));
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<string>>(context.Execution.SubtitleFiles).Count);
        Assert.Equal(
            [11L, 22L],
            Assert.IsAssignableFrom<IReadOnlyDictionary<long, string>>(
                    context.Execution.SubtitleTrackFiles)
                .Keys
                .Order()
                .ToArray());
    }

    [Fact]
    public async Task SelectedTracksWithSameLanguageDescriptionKeepSeparateFiles()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal)
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"中文","subtitle_url":"//example.test/zh.json","type":0},{"id":22,"lan":"ai-zh","lan_doc":"中文","subtitle_url":"//example.test/ai-zh.json","type":1}]}}}
                      """
                    : request.RequestAddress.EndsWith("/ai-zh.json", StringComparison.Ordinal)
                        ? """{"body":[{"from":0,"to":1,"content":"AI"}]}"""
                        : """{"body":[{"from":0,"to":1,"content":"standard"}]}""")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client, subtitle: true,
            selectedSubtitleTrackIds: [11, 22]).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var files = Assert.IsAssignableFrom<IReadOnlyDictionary<long, string>>(
            context.Execution.SubtitleTrackFiles);
        Assert.Equal(2, files.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("standard", await File.ReadAllTextAsync(
            files[11], TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("AI", await File.ReadAllTextAsync(
            files[22], TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSelectedSubtitleTrackFailsBeforeWritingAnySubtitle()
    {
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (request, _) => Task.FromResult(
                request.RequestAddress.Contains("/x/player/wbi/v2", StringComparison.Ordinal)
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/zh.json","type":0}]}}}
                      """
                    : """{"body":[{"from":0,"to":1,"content":"chinese"}]}""")
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            subtitle: true,
            selectedSubtitleTrackIds: [11, 22]).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.missing-tracks", result.Error?.Code);
        Assert.Empty(context.GetPhysicalArtifactFiles());
        Assert.Empty((await context.GetTaskAsync().ConfigureAwait(true)).Plan.TransferFiles);
    }

    [Fact]
    public async Task MalformedSubtitleIsNotReportedAsNoResource()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(request++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"ai-zh","lan_doc":"AI","subtitle_url":"//example.test/subtitle.json","type":1}]}}}
                  """
                : "{not-json")
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.parse", result.Error?.Code);
    }

    [Fact]
    public async Task SubtitleWriteFailureIsNotReportedAsNoResource()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => Task.FromResult(request++ == 0
                ? """
                  {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle.json","type":0}]}}}
                  """
                : """
                  {"body":[{"from":0,"to":1,"location":2,"content":"hello"}]}
                  """)
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            subtitle: true,
            useMissingOutputDirectory: true).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadArtifactWriter.SubtitleOutputWriteFailureCode, result.Error?.Code);
    }

    [Fact]
    public async Task SubtitleResponseReadFailureIsDistinctFromOutputWriteFailure()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => request++ == 0
                ? Task.FromResult(
                    """
                    {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle.json","type":0}]}}}
                    """)
                : Task.FromException<string>(new IOException("Response read failed."))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal(DownloadArtifactWriter.SubtitleResponseReadFailureCode, result.Error?.Code);
        Assert.NotEqual(DownloadArtifactWriter.SubtitleOutputWriteFailureCode, result.Error?.Code);
    }

    [Fact]
    public async Task SubtitleHttpStatusIsPreservedAsAStableFailureCode()
    {
        var request = 0;
        var client = new TestBilibiliApiClient
        {
            GetStringAsyncHandler = (_, _) => request++ == 0
                ? Task.FromResult(
                    """
                    {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle.json","type":0}]}}}
                    """)
                : Task.FromException<string>(new HttpRequestException(
                    "Unauthorized.",
                    null,
                    HttpStatusCode.Unauthorized))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, subtitle: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.subtitle.http-401", result.Error?.Code);
    }

    [Fact]
    public async Task DanmakuHttpFailureFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (_, _) =>
                Task.FromException<Stream>(new HttpRequestException("unavailable"))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, danmaku: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.http", result.Error?.Code);
    }

    [Fact]
    public async Task DanmakuRateLimitIsPreservedAsAStableFailureCode()
    {
        var client = new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (_, _) =>
                Task.FromException<Stream>(new HttpRequestException(
                    "Rate limited.",
                    null,
                    HttpStatusCode.TooManyRequests))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, danmaku: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.http-429", result.Error?.Code);
    }

    [Fact]
    public async Task MalformedDanmakuFailsArtifactStage()
    {
        var client = new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (_, _) =>
                Task.FromResult<Stream>(new MemoryStream([0x0A, 0x05, 0x01]))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, danmaku: true)
            .ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.artifact.danmaku.parse", result.Error?.Code);
    }

    [Fact]
    public async Task ArtifactCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, token) =>
                Task.FromException(new OperationCanceledException(token))
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/cover.jpg").ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Stage.ExecuteAsync(context.Execution, cancellation.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task SuccessfulArtifactStageAllowsFinalizeStage()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, token) =>
                File.WriteAllTextAsync(destination, "image", token)
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            coverUrl: "https://example.test/cover.jpg").ConfigureAwait(true);
        var finalized = false;

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new CallbackStage(() => finalized = true)],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(run.Result.IsSuccess);
        Assert.True(finalized);
        Assert.True(File.Exists(context.Execution.CoverFile));
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void AssertPhysicalArtifactsAreDurablyOwned(
        ArtifactTestContext context,
        DownloadTask task)
    {
        var physicalFiles = context.GetPhysicalArtifactFiles();
        IEnumerable<string> durableOwners = task.Plan.TransferFiles.Values;
        if (Environment.GetEnvironmentVariable("DOWNKYI_TEST_MUTATE_ARTIFACT_OWNER") == "1" &&
            physicalFiles.FirstOrDefault() is { } ownerToRemove)
        {
            durableOwners = durableOwners.Where(path =>
                !PathComparer.Equals(Path.GetFullPath(path), ownerToRemove));
        }

        Assert.Empty(FindUnownedArtifactFiles(
            physicalFiles,
            durableOwners));
    }

    private static string[] FindUnownedArtifactFiles(
        IEnumerable<string> physicalFiles,
        IEnumerable<string> durableOwners)
    {
        var owners = durableOwners
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToHashSet(PathComparer);
        return physicalFiles
            .Select(Path.GetFullPath)
            .Where(path => !IsDurablyOwned(path, owners))
            .OrderBy(path => path, PathComparer)
            .ToArray();
    }

    private static bool IsDurablyOwned(string path, HashSet<string> owners)
    {
        if (owners.Contains(path))
        {
            return true;
        }

        return path.EndsWith(".download", StringComparison.OrdinalIgnoreCase) &&
               owners.Contains(path[..^".download".Length]);
    }

    private static TestBilibiliApiClient CreateCoverFailureClient(CoverFailureMode failureMode)
    {
        return new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = async (_, destination, token) =>
            {
                switch (failureMode)
                {
                    case CoverFailureMode.HtmlError:
                        await File.WriteAllTextAsync(
                            destination,
                            "<!doctype html>error",
                            token).ConfigureAwait(false);
                        return;
                    case CoverFailureMode.HttpBeforeWrite:
                        throw new HttpRequestException("unavailable");
                    case CoverFailureMode.IoAfterWrite:
                        await File.WriteAllTextAsync(destination, "partial", token).ConfigureAwait(false);
                        throw new IOException("write failed");
                    case CoverFailureMode.PermissionAfterWrite:
                        await File.WriteAllTextAsync(destination, "partial", token).ConfigureAwait(false);
                        throw new UnauthorizedAccessException("denied");
                    case CoverFailureMode.CancellationAfterWrite:
                        await File.WriteAllTextAsync(destination, "partial", token).ConfigureAwait(false);
                        throw new OperationCanceledException(token);
                    default:
                        throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null);
                }
            }
        };
    }

    private static TestBilibiliApiClient CreateSuccessfulArtifactClient(ArtifactKind kind)
    {
        if (kind == ArtifactKind.Subtitle)
        {
            var request = 0;
            return new TestBilibiliApiClient
            {
                GetStringAsyncHandler = (_, _) => Task.FromResult(request++ == 0
                    ? """
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"id":11,"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle-zh.json","type":0},{"id":22,"lan":"en","lan_doc":"English","subtitle_url":"//example.test/subtitle-en.json","type":0}]}}}
                      """
                    : """
                      {"body":[{"from":0,"to":1,"location":2,"content":"hello"}]}
                      """)
            };
        }

        if (kind == ArtifactKind.Danmaku)
        {
            var payloads = new Queue<byte[]>(
            [
                new DmWebViewReply
                {
                    DmSge = new DmSegConfig { PageSize = 360_000, Total = 1 }
                }.ToByteArray(),
                new DmSegMobileReply
                {
                    Elems =
                    {
                        new DanmakuElem
                        {
                            Id = 1,
                            Progress = 1_000,
                            Mode = 1,
                            Fontsize = 25,
                            Color = 0xFFFFFF,
                            MidHash = "anonymous",
                            Content = "hello"
                        }
                    }
                }.ToByteArray()
            ]);
            return new TestBilibiliApiClient
            {
                OpenReadAsyncHandler = (_, _) =>
                    Task.FromResult<Stream>(new MemoryStream(payloads.Dequeue()))
            };
        }

        return new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, token) =>
                File.WriteAllTextAsync(destination, "image", token)
        };
    }

    private static TestBilibiliApiClient CreateDanmakuClient(
        IEnumerable<DanmakuElem> elements,
        List<string>? requests = null,
        Action? segmentStarted = null)
    {
        var segment = new DmSegMobileReply();
        segment.Elems.Add(elements);
        var payloads = new Queue<byte[]>(
        [
            new DmWebViewReply
            {
                DmSge = new DmSegConfig { PageSize = 360_000, Total = 1 }
            }.ToByteArray(),
            segment.ToByteArray()
        ]);
        return new TestBilibiliApiClient
        {
            OpenReadAsyncHandler = (request, _) =>
            {
                requests?.Add(request.RequestAddress);
                if (request.RequestAddress.Contains("/x/v2/dm/web/seg.so", StringComparison.Ordinal))
                {
                    segmentStarted?.Invoke();
                }

                return Task.FromResult<Stream>(new MemoryStream(payloads.Dequeue()));
            }
        };
    }

    private sealed class CallbackStage(Action callback) : IDownloadPipelineStage
    {
        public string Name => "finalize";

        public Task<OperationResult<DownloadStageResult>> ExecuteAsync(
            DownloadExecutionContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            callback();
            return Task.FromResult(DownloadStageResult.Success(Name));
        }
    }

    private static SqliteDownloadTaskStore CreateSqliteStore(string databasePath)
    {
        return new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(databasePath),
            new SystemClock());
    }

    private static void ClearSqlitePool(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString());
        SqliteConnection.ClearPool(connection);
    }

    private enum CoverFailureMode
    {
        HtmlError,
        HttpBeforeWrite,
        IoAfterWrite,
        PermissionAfterWrite,
        CancellationAfterWrite
    }

    private enum ArtifactKind
    {
        MainCover,
        PageCover,
        Subtitle,
        Danmaku,
        Nfo
    }

    private sealed class ArtifactTestContext : IDisposable
    {
        private readonly string _directory;
        private readonly DownKyi.Infrastructure.Settings.SettingsStore _settings;
        private readonly DownloadTaskApplicationService _tasks;
        private readonly DownloadTaskProjectionStore _projections;

        private ArtifactTestContext(
            string directory,
            DownKyi.Infrastructure.Settings.SettingsStore settings,
            DownloadTaskApplicationService tasks,
            DownloadTaskProjectionStore projections,
            DownloadingItem downloading,
            DownloadExecutionContext execution,
            DownloadArtifactsStage stage)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
            _projections = projections;
            Downloading = downloading;
            Execution = execution;
            Stage = stage;
        }

        public DownloadingItem Downloading { get; }

        public DownloadExecutionContext Execution { get; }

        public DownloadArtifactsStage Stage { get; }

        public async Task<DownloadTask> GetTaskAsync()
        {
            return await _tasks.FindAsync(
                       Execution.TaskId,
                       TestContext.Current.CancellationToken).ConfigureAwait(true)
                   ?? throw new InvalidOperationException("Test task disappeared.");
        }

        public async Task ClaimTransferFileAsync(string key, string file)
        {
            var result = await _tasks.ClaimTransferFileAsync(
                Execution.TaskId,
                key,
                file,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(result.IsSuccess, result.Error?.Message);
        }

        public ApplicationSettings SetDanmakuOutputFormat(DanmakuOutputFormat outputFormat)
        {
            return _settings.Update(current => current with
            {
                Danmaku = current.Danmaku with { OutputFormat = outputFormat }
            });
        }

        public string[] GetPhysicalArtifactFiles()
        {
            return Directory.EnumerateFiles(_directory, "output*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }

        public static async Task<ArtifactTestContext> CreateAsync(
            IBilibiliApiClient client,
            bool cover = false,
            bool subtitle = false,
            bool danmaku = false,
            bool generateMetadata = false,
            bool useMissingOutputDirectory = false,
            DanmakuOutputFormat? danmakuOutputFormat = null,
            string? coverUrl = null,
            string? pageCoverUrl = null,
            long episodeId = -1,
            PlayStreamType streamType = PlayStreamType.Video,
            long[]? selectedSubtitleTrackIds = null,
            long? defaultSubtitleTrackId = null,
            Func<DanmakuApplicationSettings, DanmakuApplicationSettings>? configureDanmaku = null,
            SubtitleLanguagePreference preferredSubtitleLanguage = SubtitleLanguagePreference.Unspecified)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new DownKyi.Infrastructure.Settings.SettingsStore(
                Path.Combine(directory, "settings.json"));
            if (danmakuOutputFormat is not null || configureDanmaku is not null)
            {
                settings.Update(current => current with
                {
                    Danmaku = configureDanmaku?.Invoke(current.Danmaku with
                    {
                        OutputFormat = danmakuOutputFormat ?? current.Danmaku.OutputFormat
                    }) ?? current.Danmaku with
                    {
                        OutputFormat = danmakuOutputFormat ?? current.Danmaku.OutputFormat
                    }
                });
            }

            if (generateMetadata)
            {
                settings.Update(current => current with
                {
                    Video = current.Video with
                    {
                        Content = current.Video.Content with
                        {
                            GenerateMovieMetadata = true
                        }
                    }
                });
            }

            var taskId = new DownloadTaskId(Guid.NewGuid().ToString("N"));
            var outputDirectory = useMissingOutputDirectory
                ? Path.Combine(directory, "missing")
                : directory;
            var downloadBase = new DownloadBase
            {
                Id = taskId.Value,
                Avid = 1,
                Bvid = "BV1test",
                Cid = 2,
                EpisodeId = episodeId,
                FilePath = Path.Combine(outputDirectory, "output"),
                CoverUrl = coverUrl ?? string.Empty,
                PageCoverUrl = pageCoverUrl ?? string.Empty
            };
            downloadBase.NeedDownloadContent = DownloadContentSelection.None with
            {
                Cover = cover,
                Subtitle = subtitle,
                Danmaku = danmaku,
                SelectedSubtitleTrackIds = selectedSubtitleTrackIds == null
                    ? null
                    : ImmutableArray.CreateRange(selectedSubtitleTrackIds),
                DefaultSubtitleTrackId = defaultSubtitleTrackId,
                PreferredSubtitleLanguage = preferredSubtitleLanguage
            };
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = taskId.Value,
                    DownloadBase = downloadBase,
                    PlayStreamType = streamType,
                    DownloadStatus = DownloadStatus.WaitForDownload
                }
            };
            if (generateMetadata)
            {
                downloading.Metadata = new MovieMetadata
                {
                    Title = "Owned metadata",
                    Plot = "Deterministic ownership fixture"
                };
            }
            var store = new SingleTaskStore();
            var historyService = DownloadHistoryService.CreateForSharedStore(store);
            var tasks = new DownloadTaskApplicationService(store, historyService, new SystemClock());
            var projections = new DownloadTaskProjectionStore(
                tasks,
                historyService,
                new SystemClock());
            var stateWriter = new DownloadTaskStateWriter(tasks);
            await projections.AddDownloadingAsync(
                downloading,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var execution = DownloadExecutionContextTestFactory.Create(
                downloading,
                settings.Current);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
            var writer = new DownloadArtifactWriter(
                new TestWbiKeyProvider(),
                stateWriter,
                NullLogger<DownloadArtifactWriter>.Instance,
                client);
            return new ArtifactTestContext(
                directory,
                settings,
                tasks,
                projections,
                downloading,
                execution,
                new DownloadArtifactsStage(
                    writer,
                    new DownloadActivityPresenter(projections, stateWriter)));
        }

        public void Dispose()
        {
            _projections.Dispose();
            _tasks.Dispose();
            _settings.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class ProductionBilibiliClientContext : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CapturingHttpClientFactory _httpClientFactory;
        private readonly CredentialProbe _credentialProbe;

        private ProductionBilibiliClientContext(
            ServiceProvider services,
            CapturingHttpClientFactory httpClientFactory,
            CredentialProbe credentialProbe,
            IBilibiliApiClient client,
            List<CapturedHttpRequest> requests)
        {
            _services = services;
            _httpClientFactory = httpClientFactory;
            _credentialProbe = credentialProbe;
            Client = client;
            Requests = requests;
        }

        public IBilibiliApiClient Client { get; }

        public IReadOnlyList<CapturedHttpRequest> Requests { get; }

        public int CookieReads => _credentialProbe.CookieReads;

        public int BuvidReads => _credentialProbe.BuvidReads;

        public static ProductionBilibiliClientContext Create()
        {
            var requests = new List<CapturedHttpRequest>();
            var httpClientFactory = new CapturingHttpClientFactory(requests);
            var credentialProbe = new CredentialProbe();
            var services = new ServiceCollection();
            services.AddDownKyiBilibiliInfrastructure(_ => new BilibiliNetworkOptions(
                "DownKyi.Tests",
                UseProxy: false,
                ProxyAddress: null));
            services.AddSingleton<IBilibiliCookieProvider>(credentialProbe);
            services.AddSingleton<IBuvidProvider>(credentialProbe);
            services.AddSingleton<IHttpClientFactory>(httpClientFactory);
            var provider = services.BuildServiceProvider();
            return new ProductionBilibiliClientContext(
                provider,
                httpClientFactory,
                credentialProbe,
                provider.GetRequiredService<IBilibiliApiClient>(),
                requests);
        }

        public void Dispose()
        {
            _services.Dispose();
            _httpClientFactory.Dispose();
        }
    }

    private sealed class CapturingHttpClientFactory : IHttpClientFactory, IDisposable
    {
        private readonly CapturingHttpMessageHandler _handler;
        private readonly HttpClient _client;

        public CapturingHttpClientFactory(List<CapturedHttpRequest> requests)
        {
            _handler = new CapturingHttpMessageHandler(requests);
            _client = new HttpClient(_handler, disposeHandler: false);
        }

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
            _handler.Dispose();
        }
    }

    private sealed class CapturingHttpMessageHandler(List<CapturedHttpRequest> requests)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(new CapturedHttpRequest(
                request.RequestUri
                ?? throw new InvalidOperationException("Request URI was missing."),
                request.Headers.Contains("cookie"),
                request.Headers.Contains("origin")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xFF, 0xD8, 0xFF])
            });
        }
    }

    private sealed record CapturedHttpRequest(Uri Address, bool HasCookie, bool HasOrigin);

    private sealed class CredentialProbe : IBilibiliCookieProvider, IBuvidProvider
    {
        public int CookieReads { get; private set; }

        public int BuvidReads { get; private set; }

        public string GetCookieHeader()
        {
            CookieReads++;
            return "SESSDATA=synthetic-secret";
        }

        public Task<BilibiliBuvid> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BuvidReads++;
            return Task.FromResult(new BilibiliBuvid("synthetic-3", "synthetic-4"));
        }
    }

    private sealed class SingleTaskStore :
        IDownloadTaskStore,
        IDownloadHistoryStore,
        IDownloadCompletionStore
    {
        private DownloadTask? _task;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<OperationResult> AddAsync(
            DownloadTask task,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _task = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> AddHistoryAsync(
            DownloadHistoryRecord history,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_task == null || _task.Version != expectedVersion)
            {
                return Task.FromResult(OperationResult.Failure(
                    OperationError.Unexpected("test.version", "Unexpected task version.")));
            }

            _task = task;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> CompleteAsync(
            DownloadTask task,
            DownloadHistoryRecord history,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            _task = null;
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<DownloadTask?> FindAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_task?.Id == taskId ? _task : null);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DownloadTask>>(_task == null ? [] : [_task]);

        public Task<bool> IsOutputPathReservedAsync(
            string basePath,
            bool ignoreCase,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetActiveOutputReservationKeysAsync(
            bool ignoreCase,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> DeleteHistoryAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success());

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>([]);
    }
}
