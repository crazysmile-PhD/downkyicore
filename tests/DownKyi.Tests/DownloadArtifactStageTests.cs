using Bilibili.Community.Service.Dm.V1;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadArtifactStageTests
{
    [Fact]
    public async Task CoverHttpFailureStopsBeforeFinalize()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                Task.FromException(new HttpRequestException("unavailable"))
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
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
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.bin";

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
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.bin";

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
            if (failureMode == CoverFailureMode.IoAfterWrite)
            {
                Assert.Equal("download.artifact.cover.io", result.Error?.Code);
            }
        }

        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        if (failureMode == CoverFailureMode.HtmlError)
        {
            Assert.Single(context.GetPhysicalArtifactFiles());
        }
        else
        {
            Assert.Empty(context.GetPhysicalArtifactFiles());
        }

        Assert.Empty(context.GetTemporaryOutputFiles());
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
            generateMetadata: kind == ArtifactKind.Nfo).ConfigureAwait(true);

        if (kind == ArtifactKind.MainCover)
        {
            context.Downloading.DownloadBase.CoverUrl = "https://example.test/main.bin";
        }
        else if (kind == ArtifactKind.PageCover)
        {
            context.Downloading.DownloadBase.PageCoverUrl = "https://example.test/page.bin";
        }

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        AssertPhysicalArtifactsAreDurablyOwned(context, task);
        Assert.NotEmpty(context.GetPhysicalArtifactFiles());
        Assert.Empty(context.GetTemporaryOutputFiles());
        if (kind == ArtifactKind.Subtitle)
        {
            Assert.Contains(DownloadArtifactWriter.DefaultSubtitleTransferKey, task.Plan.TransferFiles.Keys);
            Assert.Contains(DownloadArtifactWriter.GetSubtitleTrackTransferKey(0), task.Plan.TransferFiles.Keys);
            Assert.Contains(DownloadArtifactWriter.GetSubtitleTrackTransferKey(1), task.Plan.TransferFiles.Keys);
            Assert.Equal(3, context.GetPhysicalArtifactFiles().Length);
        }
    }

    [Fact]
    public async Task OwnershipOracleRejectsSyntheticMissingOwnerMutation()
    {
        var client = CreateSuccessfulArtifactClient(ArtifactKind.MainCover);
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/main.bin";
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

    [Fact]
    public async Task PageAndMainCoversPersistUnderDistinctStableKeys()
    {
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, destination, token) =>
                File.WriteAllTextAsync(destination, "image", token)
        };
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.PageCoverUrl = "https://example.test/page.jpg";
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/main.png";

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        var task = await context.GetTaskAsync().ConfigureAwait(true);
        Assert.Equal(
            context.Execution.PageCoverFile,
            task.Plan.TransferFiles[DownloadArtifactWriter.PageCoverTransferKey]);
        Assert.Equal(
            context.Execution.CoverFile,
            task.Plan.TransferFiles[DownloadArtifactWriter.MainCoverTransferKey]);
        Assert.NotEqual(context.Execution.PageCoverFile, context.Execution.CoverFile);
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
        Assert.False(second.IsSuccess);
        Assert.Equal("download.output.destination-collision", second.Error?.Code);
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
        Assert.Equal("download.artifact.subtitle.io", result.Error?.Code);
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
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
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
        using var context = await ArtifactTestContext.CreateAsync(client, cover: true)
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
        var finalized = false;

        var run = await DownloadPipeline.ExecuteStagesAsync(
            [context.Stage, new CallbackStage(() => finalized = true)],
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(run.Result.IsSuccess);
        Assert.True(finalized);
        Assert.True(File.Exists(context.Execution.CoverFile));
    }

    [Theory]
    [InlineData(nameof(ArtifactKind.MainCover))]
    [InlineData(nameof(ArtifactKind.Subtitle))]
    [InlineData(nameof(ArtifactKind.Danmaku))]
    [InlineData(nameof(ArtifactKind.Nfo))]
    public async Task LateDestinationCollisionPreservesForeignArtifactAndCleansTemporaryFile(
        string kindName)
    {
        var kind = Enum.Parse<ArtifactKind>(kindName);
        string? foreignPath = null;
        var publisher = new AtomicOutputPublisher(path =>
        {
            foreignPath = path;
            File.WriteAllText(path, "foreign artifact");
        });
        var client = CreateSuccessfulArtifactClient(kind);
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: kind == ArtifactKind.MainCover,
            subtitle: kind == ArtifactKind.Subtitle,
            danmaku: kind == ArtifactKind.Danmaku,
            generateMetadata: kind == ArtifactKind.Nfo,
            outputPublisher: publisher).ConfigureAwait(true);
        if (kind == ArtifactKind.MainCover)
        {
            context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";
        }

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.output.destination-collision", result.Error?.Code);
        var path = Assert.IsType<string>(foreignPath);
        Assert.Equal("foreign artifact", await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Empty(context.GetTemporaryOutputFiles());
    }

    [Fact]
    public async Task DefaultSubtitleLateDestinationCollisionPreservesForeignFileAndCleansTemporaryFile()
    {
        string? foreignPath = null;
        var publisher = new AtomicOutputPublisher(path =>
        {
            if (!string.Equals(Path.GetFileName(path), "output.srt", StringComparison.Ordinal))
            {
                return;
            }

            foreignPath = path;
            File.WriteAllText(path, "foreign subtitle");
        });
        using var context = await ArtifactTestContext.CreateAsync(
            CreateSuccessfulArtifactClient(ArtifactKind.Subtitle),
            subtitle: true,
            outputPublisher: publisher).ConfigureAwait(true);

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.output.destination-collision", result.Error?.Code);
        var path = Assert.IsType<string>(foreignPath);
        Assert.Equal("foreign subtitle", await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Empty(context.GetTemporaryOutputFiles());
    }

    [Fact]
    public async Task AtomicPublisherReturnsEvidenceOnlyAfterTheDestinationIsPublishedAndVerified()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-atomic-output-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "output.mp4");
            var ownershipProvider = new RecordingPublicationOwnershipProvider();
            var publisher = new AtomicOutputPublisher(ownershipProvider);

            var result = await publisher.PublishAsync(
                destination,
                (temporaryPath, token) => File.WriteAllTextAsync(
                    temporaryPath,
                    "published media",
                    token),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(result.Succeeded);
            Assert.Same(ownershipProvider.Evidence, result.PublicationEvidence);
            Assert.Equal("published media", await File.ReadAllTextAsync(
                destination,
                TestContext.Current.CancellationToken).ConfigureAwait(true));
            var capture = Assert.Single(ownershipProvider.Captures);
            Assert.True(capture.ExistedWhenObserved);
            Assert.Contains(".downkyi-tmp", capture.Path, StringComparison.Ordinal);
            var verification = Assert.Single(ownershipProvider.Verifications);
            Assert.Equal(Path.GetFullPath(destination), verification.Path);
            Assert.True(verification.ExistedWhenObserved);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AtomicPublisherLeavesOutputUntrackedWhenPostMoveIdentityVerificationFails()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "downkyi-atomic-output-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "output.mp4");
            var ownershipProvider = new RecordingPublicationOwnershipProvider
            {
                VerifyPublishedIdentity = false
            };
            var publisher = new AtomicOutputPublisher(ownershipProvider);

            var result = await publisher.PublishAsync(
                destination,
                (temporaryPath, token) => File.WriteAllTextAsync(
                    temporaryPath,
                    "published media",
                    token),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(result.Succeeded);
            Assert.Null(result.PublicationEvidence);
            Assert.True(File.Exists(destination));
            Assert.Single(ownershipProvider.Captures);
            Assert.Single(ownershipProvider.Verifications);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(nameof(ArtifactKind.MainCover))]
    [InlineData(nameof(ArtifactKind.PageCover))]
    [InlineData(nameof(ArtifactKind.Subtitle))]
    [InlineData(nameof(ArtifactKind.Danmaku))]
    [InlineData(nameof(ArtifactKind.Nfo))]
    public async Task SuccessfulArtifactPublicationsPersistProvenanceAfterEachOutputIsPublished(
        string kindName)
    {
        var kind = Enum.Parse<ArtifactKind>(kindName);
        var provenance = new RecordingOutputProvenanceService();
        var ownershipProvider = new RecordingPublicationOwnershipProvider();
        var recorder = new DownloadOutputArtifactProvenanceRecorder(
            provenance,
            NullLogger<DownloadOutputArtifactProvenanceRecorder>.Instance);
        using var context = await ArtifactTestContext.CreateAsync(
            CreateSuccessfulArtifactClient(kind),
            cover: kind is ArtifactKind.MainCover or ArtifactKind.PageCover,
            subtitle: kind == ArtifactKind.Subtitle,
            danmaku: kind == ArtifactKind.Danmaku,
            generateMetadata: kind == ArtifactKind.Nfo,
            outputPublisher: new AtomicOutputPublisher(ownershipProvider),
            provenanceRecorder: recorder).ConfigureAwait(true);
        if (kind == ArtifactKind.MainCover)
        {
            context.Downloading.DownloadBase.CoverUrl = "https://example.test/main.jpg";
        }
        else if (kind == ArtifactKind.PageCover)
        {
            context.Downloading.DownloadBase.PageCoverUrl = "https://example.test/page.jpg";
        }

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        (string ArtifactKey, string ArtifactKind)[] expected = kind switch
        {
            ArtifactKind.MainCover =>
            new[]
            {
                (DownloadArtifactWriter.MainCoverTransferKey, DownloadArtifactWriter.CoverArtifactKind)
            },
            ArtifactKind.PageCover =>
            new[]
            {
                (DownloadArtifactWriter.PageCoverTransferKey, DownloadArtifactWriter.PageCoverArtifactKind)
            },
            ArtifactKind.Subtitle =>
            new[]
            {
                (DownloadArtifactWriter.GetSubtitleTrackTransferKey(0), DownloadArtifactWriter.SubtitleArtifactKind),
                (DownloadArtifactWriter.GetSubtitleTrackTransferKey(1), DownloadArtifactWriter.SubtitleArtifactKind),
                (DownloadArtifactWriter.DefaultSubtitleTransferKey, DownloadArtifactWriter.SubtitleArtifactKind)
            },
            ArtifactKind.Danmaku =>
            new[]
            {
                ("danmaku", DownloadArtifactWriter.DanmakuArtifactKind)
            },
            ArtifactKind.Nfo =>
            new[]
            {
                ("nfo", DownloadArtifactWriter.NfoArtifactKind)
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kindName), kind, null)
        };

        Assert.Equal(expected, provenance.Persisted
            .Select(record => (record.ArtifactKey, record.ArtifactKind))
            .ToArray());
        Assert.Equal(expected.Length, ownershipProvider.Captures.Count);
        Assert.Equal(expected.Length, ownershipProvider.Verifications.Count);
        Assert.All(provenance.Persisted, record =>
        {
            Assert.Equal(context.Execution.TaskId, record.TaskId);
            Assert.True(record.DestinationExistedWhenRecorded);
            Assert.True(File.Exists(record.CanonicalPath));
            Assert.Same(ownershipProvider.Evidence, record.PublicationEvidence);
        });
    }

    [Fact]
    public async Task FailedArtifactPublicationDoesNotRecordProvenance()
    {
        var provenance = new RecordingOutputProvenanceService();
        var ownershipProvider = new RecordingPublicationOwnershipProvider();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, _) =>
                Task.FromException(new IOException("write failed"))
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            outputPublisher: new AtomicOutputPublisher(ownershipProvider),
            provenanceRecorder: new DownloadOutputArtifactProvenanceRecorder(
                provenance,
                NullLogger<DownloadOutputArtifactProvenanceRecorder>.Instance))
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Empty(provenance.Attempts);
        Assert.Empty(provenance.Persisted);
        Assert.Empty(ownershipProvider.Captures);
        Assert.False(File.Exists(context.Execution.CoverFile));
    }

    [Fact]
    public async Task DestinationCollisionDoesNotRecordProvenance()
    {
        string? foreignPath = null;
        var provenance = new RecordingOutputProvenanceService();
        var ownershipProvider = new RecordingPublicationOwnershipProvider();
        var publisher = new AtomicOutputPublisher(
            path =>
            {
                foreignPath = path;
                File.WriteAllText(path, "foreign artifact");
            },
            ownershipProvider);
        using var context = await ArtifactTestContext.CreateAsync(
            CreateSuccessfulArtifactClient(ArtifactKind.MainCover),
            cover: true,
            outputPublisher: publisher,
            provenanceRecorder: new DownloadOutputArtifactProvenanceRecorder(
                provenance,
                NullLogger<DownloadOutputArtifactProvenanceRecorder>.Instance))
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.IsSuccess);
        Assert.Equal("download.output.destination-collision", result.Error?.Code);
        Assert.Empty(provenance.Attempts);
        Assert.Empty(provenance.Persisted);
        Assert.Single(ownershipProvider.Captures);
        Assert.Empty(ownershipProvider.Verifications);
        Assert.Equal("foreign artifact", await File.ReadAllTextAsync(
            Assert.IsType<string>(foreignPath),
            TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task CanceledArtifactPublicationDoesNotRecordProvenance()
    {
        var provenance = new RecordingOutputProvenanceService();
        var ownershipProvider = new RecordingPublicationOwnershipProvider();
        var client = new TestBilibiliApiClient
        {
            DownloadFileAsyncHandler = (_, _, token) =>
                Task.FromException(new OperationCanceledException(token))
        };
        using var context = await ArtifactTestContext.CreateAsync(
            client,
            cover: true,
            outputPublisher: new AtomicOutputPublisher(ownershipProvider),
            provenanceRecorder: new DownloadOutputArtifactProvenanceRecorder(
                provenance,
                NullLogger<DownloadOutputArtifactProvenanceRecorder>.Instance))
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Stage.ExecuteAsync(
                context.Execution,
                TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Empty(provenance.Attempts);
        Assert.Empty(provenance.Persisted);
        Assert.Empty(ownershipProvider.Captures);
        Assert.False(File.Exists(context.Execution.CoverFile));
    }

    [Fact]
    public async Task ProvenancePersistenceFailureLeavesSuccessfulOutputUntracked()
    {
        var provenance = new RecordingOutputProvenanceService
        {
            RecordResult = OperationResult.Failure(
                OperationError.Unexpected(
                    "download.output_provenance.write_failed",
                    "The durable provenance write failed."))
        };
        var ownershipProvider = new RecordingPublicationOwnershipProvider();
        using var context = await ArtifactTestContext.CreateAsync(
            CreateSuccessfulArtifactClient(ArtifactKind.MainCover),
            cover: true,
            outputPublisher: new AtomicOutputPublisher(ownershipProvider),
            provenanceRecorder: new DownloadOutputArtifactProvenanceRecorder(
                provenance,
                NullLogger<DownloadOutputArtifactProvenanceRecorder>.Instance))
            .ConfigureAwait(true);
        context.Downloading.DownloadBase.CoverUrl = "https://example.test/cover.jpg";

        var result = await context.Stage.ExecuteAsync(
            context.Execution,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(provenance.Attempts);
        Assert.Empty(provenance.Persisted);
        Assert.True(File.Exists(context.Execution.CoverFile));
        Assert.Single(ownershipProvider.Captures);
        Assert.Single(ownershipProvider.Verifications);
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

        return (path.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase) &&
                owners.Contains(path[..^".aria2".Length])) ||
               (path.EndsWith(".download", StringComparison.OrdinalIgnoreCase) &&
                owners.Contains(path[..^".download".Length]));
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
                      {"code":0,"data":{"aid":1,"bvid":"BV1test","cid":2,"subtitle":{"subtitles":[{"lan":"zh","lan_doc":"Chinese","subtitle_url":"//example.test/subtitle-zh.json","type":0},{"lan":"en","lan_doc":"English","subtitle_url":"//example.test/subtitle-en.json","type":0}]}}}
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

    private sealed record PublicationPathObservation(
        string Path,
        bool ExistedWhenObserved);

    private sealed record RecordedArtifactPublication(
        DownloadTaskId TaskId,
        string ArtifactKey,
        string ArtifactKind,
        string CanonicalPath,
        OutputArtifactPublicationEvidence PublicationEvidence,
        bool DestinationExistedWhenRecorded);

    private sealed class RecordingOutputProvenanceService
        : IDownloadOutputArtifactProvenanceApplicationService
    {
        public List<RecordedArtifactPublication> Attempts { get; } = [];

        public List<RecordedArtifactPublication> Persisted { get; } = [];

        public OperationResult RecordResult { get; init; } = OperationResult.Success();

        public Task<OperationResult> RecordPublishedAsync(
            DownloadTaskId taskId,
            string artifactKey,
            string artifactKind,
            string canonicalPath,
            OutputArtifactPublicationEvidence publicationEvidence,
            CancellationToken cancellationToken)
        {
            var record = new RecordedArtifactPublication(
                taskId,
                artifactKey,
                artifactKind,
                Path.GetFullPath(canonicalPath),
                publicationEvidence,
                File.Exists(canonicalPath));
            Attempts.Add(record);
            if (RecordResult.IsSuccess)
            {
                Persisted.Add(record);
            }

            return Task.FromResult(RecordResult);
        }

        public Task<OperationResult<IReadOnlyList<DownloadOutputArtifactProvenance>>> GetPublishedAsync(
            DownloadTaskId taskId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(
                OperationResult.Success<IReadOnlyList<DownloadOutputArtifactProvenance>>([]));
        }
    }

    private sealed class RecordingPublicationOwnershipProvider : IOutputArtifactOwnershipProvider
    {
        public OutputArtifactPublicationEvidence Evidence { get; } = new(
            ByteLength: 42,
            Sha256: new string('a', 64),
            IdentityProvider: "test-publication-provider",
            FilesystemIdentity: "test-file-identity");

        public List<PublicationPathObservation> Captures { get; } = [];

        public List<PublicationPathObservation> Verifications { get; } = [];

        public bool VerifyPublishedIdentity { get; init; } = true;

        public Task<OutputArtifactEvidenceCaptureResult> CapturePublicationEvidenceAsync(
            string temporaryPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Captures.Add(new PublicationPathObservation(
                Path.GetFullPath(temporaryPath),
                File.Exists(temporaryPath)));
            return Task.FromResult(OutputArtifactEvidenceCaptureResult.Captured(Evidence));
        }

        public Task<bool> VerifyPublishedObjectIdentityAsync(
            string destinationPath,
            OutputArtifactPublicationEvidence evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Verifications.Add(new PublicationPathObservation(
                Path.GetFullPath(destinationPath),
                File.Exists(destinationPath)));
            return Task.FromResult(VerifyPublishedIdentity);
        }

        public Task<OutputArtifactSafeDeleteResult> DeleteIfOwnedAsync(
            string candidatePath,
            DownloadOutputArtifactProvenance provenance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OutputArtifactSafeDeleteResult.Unsupported());
        }
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
        private readonly DownKyi.Core.Settings.SettingsStore _settings;
        private readonly DownloadTaskApplicationService _tasks;

        private ArtifactTestContext(
            string directory,
            DownKyi.Core.Settings.SettingsStore settings,
            DownloadTaskApplicationService tasks,
            DownloadingItem downloading,
            DownloadExecutionContext execution,
            DownloadArtifactsStage stage)
        {
            _directory = directory;
            _settings = settings;
            _tasks = tasks;
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

        public string[] GetPhysicalArtifactFiles()
        {
            return Directory.EnumerateFiles(_directory, "output*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, PathComparer)
                .ToArray();
        }

        public string[] GetTemporaryOutputFiles()
        {
            return Directory.EnumerateFiles(_directory, "*.downkyi-tmp*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .ToArray();
        }

        public static async Task<ArtifactTestContext> CreateAsync(
            TestBilibiliApiClient client,
            bool cover = false,
            bool subtitle = false,
            bool danmaku = false,
            bool generateMetadata = false,
            bool useMissingOutputDirectory = false,
            IAtomicOutputPublisher? outputPublisher = null,
            DownloadOutputArtifactProvenanceRecorder? provenanceRecorder = null)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "downkyi-artifact-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var settings = new DownKyi.Core.Settings.SettingsStore(
                Path.Combine(directory, "settings.json"));
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
                FilePath = Path.Combine(outputDirectory, "output")
            };
            foreach (var key in downloadBase.NeedDownloadContent.Keys.ToArray())
            {
                downloadBase.NeedDownloadContent[key] = false;
            }

            downloadBase.NeedDownloadContent["downloadCover"] = cover;
            downloadBase.NeedDownloadContent["downloadSubtitle"] = subtitle;
            downloadBase.NeedDownloadContent["downloadDanmaku"] = danmaku;
            var downloading = new DownloadingItem
            {
                DownloadBase = downloadBase,
                Downloading = new Downloading
                {
                    Id = taskId.Value,
                    DownloadBase = downloadBase,
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
            var tasks = new DownloadTaskApplicationService(store, new SystemClock());
            var stateWriter = new DownloadTaskStateWriter(tasks);
            var task = DownloadTaskProjectionMapper.CreateNewTask(
                downloading,
                DateTimeOffset.UnixEpoch);
            Assert.True((await tasks.AddAsync(
                task,
                TestContext.Current.CancellationToken).ConfigureAwait(true)).IsSuccess);
            await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            downloading.Downloading.DownloadStatus = DownloadStatus.Downloading;
            var writer = new DownloadArtifactWriter(
                new TestWbiKeyProvider(),
                stateWriter,
                NullLogger<DownloadArtifactWriter>.Instance,
                client,
                outputPublisher,
                provenanceRecorder);
            var execution = new DownloadExecutionContext(
                taskId,
                downloading,
                settings.Current,
                static (_, token) => token.ThrowIfCancellationRequested());
            return new ArtifactTestContext(
                directory,
                settings,
                tasks,
                downloading,
                execution,
                new DownloadArtifactsStage(writer));
        }

        public void Dispose()
        {
            _tasks.Dispose();
            _settings.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class SingleTaskStore : IDownloadTaskStore
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

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadHistoryPage([], null));

        public Task<OperationResult> DeleteAsync(
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
