using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class BuiltinRangeDownloaderTests
{
    [Fact]
    public async Task ExistingCompletedTargetIsRecoveredWithoutAnotherRequest()
    {
        var payload = CreatePayload(12);
        using var handler = new UnexpectedRequestHandler();
        var directory = CreateTemporaryDirectory("range-completed-recovery");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await File.WriteAllBytesAsync(
                target,
                payload,
                TestContext.Current.CancellationToken);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4);

            var result = await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload.Length, result.TotalBytes);
            Assert.Equal(payload.Length, result.ReceivedBytes);
            Assert.Equal(0, handler.Requests);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FreshDownloadWithoutValidatorsUsesOneRangeAndProducesExactFile()
    {
        var payload = CreatePayload(12);
        using var handler = new RangePayloadHandler(
            payload,
            entityTag: null);
        var directory = CreateTemporaryDirectory("range-workers");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            int? plannedChunkCount = null;
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4,
                planCreated: count => plannedChunkCount = count);

            var result = await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload.Length, result.TotalBytes);
            Assert.Equal(payload.Length, result.ReceivedBytes);
            Assert.Equal(1, plannedChunkCount);
            Assert.Equal([(0L, 11L)], handler.ChunkRanges);
            Assert.All(handler.IfRangeValues, Assert.Null);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FreshDownloadWithStrongValidatorUsesParallelRangesAndProducesExactFile()
    {
        var payload = CreatePayload(12);
        using var handler = new RangePayloadHandler(
            payload,
            requiredConcurrentReads: 2);
        var directory = CreateTemporaryDirectory("range-workers-validated");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            int? plannedChunkCount = null;
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4,
                planCreated: count => plannedChunkCount = count);

            var result = await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload.Length, result.TotalBytes);
            Assert.Equal(payload.Length, result.ReceivedBytes);
            Assert.Equal(3, plannedChunkCount);
            Assert.Equal(2, handler.MaximumConcurrentReads);
            Assert.Equal(
                [(0L, 3L), (4L, 7L), (8L, 11L)],
                handler.ChunkRanges.OrderBy(static range => range.Start).ToArray());
            Assert.All(
                handler.IfRangeValues,
                value => Assert.Equal("\"resource-v1\"", value));
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SmallValidatedFileStillUsesConfiguredParallelism()
    {
        var payload = CreatePayload(8);
        using var handler = new RangePayloadHandler(payload);
        var directory = CreateTemporaryDirectory("range-small-parallel");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 4,
                segmentSize: 20);

            await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [(0L, 1L), (2L, 3L), (4L, 5L), (6L, 7L)],
                handler.ChunkRanges.OrderBy(static range => range.Start).ToArray());
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadBodyReadsUseTheSharedBandwidthLease()
    {
        var payload = Enumerable.Range(0, 2048)
            .Select(static value => checked((byte)(value % 251)))
            .ToArray();
        using var handler = new RangePayloadHandler(payload);
        var clock = new AdvancingTimeProvider(DateTimeOffset.UnixEpoch);
        var delays = new List<TimeSpan>();
        var limiter = new DownloadBandwidthLimiter(
            static () => new DownloadBandwidthLimits(0, 10 * 1024),
            clock,
            (delay, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                delays.Add(delay);
                clock.Advance(delay);
                return Task.CompletedTask;
            });
        var directory = CreateTemporaryDirectory("range-bandwidth-limit");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: payload.Length,
                bandwidthLimiter: limiter.CreateTransferLease());

            var result = await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(payload.Length, result.ReceivedBytes);
            Assert.Equal(TimeSpan.FromMilliseconds(100), Assert.Single(delays));
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MultiRangeCheckpointIsRejectedWhenServerStopsSupportingRanges()
    {
        var payload = CreatePayload(12);
        var directory = CreateTemporaryDirectory("range-support-removed");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                TotalFileSize = payload.Length,
                Chunks = new[]
                {
                    new { Start = 0, End = 3, Position = 0 },
                    new { Start = 4, End = 7, Position = 0 },
                    new { Start = 8, End = 11, Position = 0 }
                },
                ResourceIdentity = ResourceIdentity(strongETag: null)
            });
            var stream = new FileStream(
                $"{target}.download",
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(true))
            {
                stream.SetLength(payload.Length);
                stream.Position = payload.Length;
                await stream.WriteAsync(metadata, TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
            }

            using var handler = new NonRangePayloadHandler(payload);
            using var downloader = CreateDownloader(handler, parallelCount: 2, segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InterruptedRangeWithoutValidatorRejectsChangedSameLengthResource()
    {
        var previousPayload = CreatePayload(12);
        var currentPayload = previousPayload
            .Select(static value => checked((byte)(value + 32)))
            .ToArray();
        var directory = CreateTemporaryDirectory("range-interrupted-unvalidated");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using (var firstHandler = new RangePayloadHandler(
                previousPayload,
                failOnceAtStart: 0,
                failureBytes: 2,
                entityTag: null))
            using (var firstAttempt = CreateDownloader(
                firstHandler,
                parallelCount: 1,
                segmentSize: 4))
            {
                await Assert.ThrowsAsync<HttpIOException>(() => firstAttempt.DownloadAsync(
                    target,
                    previousPayload.Length,
                    TestContext.Current.CancellationToken));

                Assert.Equal([(0L, 11L)], firstHandler.ChunkRanges);
            }

            Assert.True(File.Exists($"{target}.download"));
            using var secondHandler = new RangePayloadHandler(currentPayload, entityTag: null);
            using var secondAttempt = CreateDownloader(
                secondHandler,
                parallelCount: 1,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => secondAttempt.DownloadAsync(
                target,
                currentPayload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 1L)], secondHandler.ChunkRanges);
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeWithMatchingStrongEntityTagRequestsOnlyMissingRangeBytes()
    {
        var payload = CreatePayload(12);
        var directory = CreateTemporaryDirectory("range-resume");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using (var firstHandler = new RangePayloadHandler(
                payload,
                failOnceAtStart: 4,
                failureBytes: 2))
            using (var firstAttempt = CreateDownloader(
                firstHandler,
                parallelCount: 1,
                segmentSize: 4))
            {
                await Assert.ThrowsAsync<HttpIOException>(() => firstAttempt.DownloadAsync(
                    target,
                    payload.Length,
                    TestContext.Current.CancellationToken));
            }

            Assert.True(File.Exists($"{target}.download"));
            using var secondHandler = new RangePayloadHandler(payload);
            using var secondAttempt = CreateDownloader(
                secondHandler,
                parallelCount: 1,
                segmentSize: 4);

            await secondAttempt.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(secondHandler.ChunkRanges, range => range.Start is 0 or 4);
            Assert.Contains(secondHandler.ChunkRanges, range => range == (6L, 7L));
            Assert.Contains(secondHandler.ChunkRanges, range => range == (8L, 11L));
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedEntityTagRejectsSameLengthPartialBeforeRequestingMissingRanges()
    {
        var previousPayload = CreatePayload(12);
        var currentPayload = previousPayload.Select(static value => checked((byte)(value + 32))).ToArray();
        var directory = CreateTemporaryDirectory("range-resource-etag-change");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                previousPayload,
                ResourceIdentity("\"resource-v1\""));
            using var handler = new RangePayloadHandler(
                currentPayload,
                entityTag: "\"resource-v2\"");
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                currentPayload.Length,
                TestContext.Current.CancellationToken));

            Assert.Empty(handler.ChunkRanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeWithoutValidatorUsesMatchingOverlapBytes()
    {
        var payload = CreatePayload(12);
        var directory = CreateTemporaryDirectory("range-overlap-match");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                payload,
                ResourceIdentity(strongETag: null),
                secondChunkPosition: 2);
            using var handler = new RangePayloadHandler(payload, entityTag: null);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4);

            await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [(0L, 3L), (4L, 5L), (6L, 7L), (8L, 11L)],
                handler.ChunkRanges.OrderBy(static range => range.Start).ToArray());
            Assert.All(handler.IfRangeValues, Assert.Null);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeWithoutValidatorRejectsMismatchInEarlierScatteredChunk()
    {
        var previousPayload = CreatePayload(12);
        var currentPayload = previousPayload.ToArray();
        currentPayload[0] = checked((byte)(currentPayload[0] + 32));
        var directory = CreateTemporaryDirectory("range-scattered-overlap-mismatch");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                previousPayload,
                ResourceIdentity(strongETag: null),
                thirdChunkPosition: 4);
            using var handler = new RangePayloadHandler(currentPayload, entityTag: null);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                currentPayload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 3L)], handler.ChunkRanges);
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeWithoutValidatorValidatesBytesBeforeTheFormerOverlapWindow()
    {
        var previousPayload = Enumerable.Range(0, 128 * 1024)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        var currentPayload = previousPayload.ToArray();
        currentPayload[0] = checked((byte)(currentPayload[0] + 1));
        const int retainedLength = 96 * 1024;
        var directory = CreateTemporaryDirectory("range-full-retained-mismatch");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WriteSingleChunkPartialAsync(
                target,
                previousPayload,
                ResourceIdentity(strongETag: null),
                retainedLength);
            using var handler = new RangePayloadHandler(currentPayload, entityTag: null);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: currentPayload.Length);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                currentPayload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, retainedLength - 1L)], handler.ChunkRanges);
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeWithoutValidatorRejectsMismatchingOverlapBytes()
    {
        var previousPayload = CreatePayload(12);
        var currentPayload = previousPayload
            .Select(static value => checked((byte)(value + 32)))
            .ToArray();
        var directory = CreateTemporaryDirectory("range-overlap-mismatch");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                previousPayload,
                ResourceIdentity(strongETag: null));
            using var handler = new RangePayloadHandler(currentPayload, entityTag: null);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 2,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                currentPayload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 3L)], handler.ChunkRanges);
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConflictingOverlapValidatorRejectsPartialWhenProbeHasNoValidator()
    {
        var payload = CreatePayload(12);
        var directory = CreateTemporaryDirectory("range-overlap-validator-mismatch");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                payload,
                ResourceIdentity("\"resource-v1\""));
            using var handler = new RangePayloadHandler(
                payload,
                entityTag: null,
                overlapEntityTag: "\"resource-v2\"");
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 3L)], handler.ChunkRanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedResourceScopeUsesOverlapBeforeResuming()
    {
        var payload = CreatePayload(12);
        var previousAddress = new Uri("https://previous-media.invalid/file");
        var directory = CreateTemporaryDirectory("range-resource-scope-change");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                payload,
                ResourceIdentity("\"resource-v1\"", previousAddress));
            using var handler = new RangePayloadHandler(payload);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [(0L, 3L), (4L, 7L), (8L, 11L)],
                handler.ChunkRanges.OrderBy(static range => range.Start).ToArray());
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MatchingStrongLastModifiedResumesOnlyMissingRangeBytes()
    {
        var payload = CreatePayload(12);
        var lastModified = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var responseDate = lastModified.AddMinutes(1);
        var directory = CreateTemporaryDirectory("range-last-modified-resume");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using (var firstHandler = new RangePayloadHandler(
                payload,
                failOnceAtStart: 4,
                failureBytes: 2,
                entityTag: null,
                lastModified: lastModified,
                responseDate: responseDate))
            using (var firstAttempt = CreateDownloader(
                firstHandler,
                parallelCount: 1,
                segmentSize: 4))
            {
                await Assert.ThrowsAsync<HttpIOException>(() => firstAttempt.DownloadAsync(
                    target,
                    payload.Length,
                    TestContext.Current.CancellationToken));
            }

            using var secondHandler = new RangePayloadHandler(
                payload,
                entityTag: null,
                lastModified: lastModified,
                responseDate: responseDate);
            using var secondAttempt = CreateDownloader(
                secondHandler,
                parallelCount: 1,
                segmentSize: 4);

            await secondAttempt.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(secondHandler.ChunkRanges, range => range.Start is 0 or 4);
            Assert.Contains(secondHandler.ChunkRanges, range => range == (6L, 7L));
            Assert.All(
                secondHandler.IfRangeValues,
                value => Assert.Equal(lastModified.ToString("R"), value));
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MatchingStrongEntityTagOverridesLastModifiedDriftWhenResuming()
    {
        var payload = CreatePayload(12);
        var previousLastModified = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var currentLastModified = previousLastModified.AddMinutes(1);
        var directory = CreateTemporaryDirectory("range-etag-precedence");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using (var firstHandler = new RangePayloadHandler(
                payload,
                failOnceAtStart: 4,
                failureBytes: 2,
                lastModified: previousLastModified,
                responseDate: previousLastModified.AddMinutes(1)))
            using (var firstAttempt = CreateDownloader(
                firstHandler,
                parallelCount: 1,
                segmentSize: 4))
            {
                await Assert.ThrowsAsync<HttpIOException>(() => firstAttempt.DownloadAsync(
                    target,
                    payload.Length,
                    TestContext.Current.CancellationToken));
            }

            using var secondHandler = new RangePayloadHandler(
                payload,
                lastModified: currentLastModified,
                responseDate: currentLastModified.AddMinutes(1));
            using var secondAttempt = CreateDownloader(
                secondHandler,
                parallelCount: 1,
                segmentSize: 4);

            await secondAttempt.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain(secondHandler.ChunkRanges, range => range.Start is 0 or 4);
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LastModifiedInsideClockSkewWindowIsNotUsedAsStrongValidator()
    {
        var lastModified = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([0])
        };
        response.Content.Headers.LastModified = lastModified;
        response.Headers.Date = lastModified.AddSeconds(59);

        var identity = BuiltinRangeResourceIdentity.Create(MediaAddress, response);

        Assert.Null(identity.LastModified);
        Assert.Null(identity.IfRangeValue);
    }

    [Fact]
    public async Task ChangedChunkEntityTagIsRejectedWhenServerIgnoresIfRange()
    {
        var payload = CreatePayload(12);
        using var handler = new RangePayloadHandler(
            payload,
            entityTag: "\"resource-v1\"",
            chunkEntityTag: "\"resource-v2\"");
        var directory = CreateTemporaryDirectory("range-if-range-ignored");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 3L)], handler.ChunkRanges);
            Assert.Equal(["\"resource-v1\""], handler.IfRangeValues);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OverlapVerifiedResumeAllowsLaterResponsesWithoutValidators()
    {
        var payload = CreatePayload(12);
        var previousAddress = new Uri("https://previous-media.invalid/file");
        var directory = CreateTemporaryDirectory("range-if-range-omitted-validator");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                payload,
                ResourceIdentity("\"resource-v1\"", previousAddress));
            using var handler = new RangePayloadHandler(
                payload,
                omitChunkValidators: true);
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken);

            Assert.Equal(
                [(0L, 3L), (4L, 7L), (8L, 11L)],
                handler.ChunkRanges.OrderBy(static range => range.Start).ToArray());
            Assert.Equal(payload, await File.ReadAllBytesAsync(
                target,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OverlapVerifiedResumeRejectsConflictingLaterValidator()
    {
        var payload = CreatePayload(12);
        var previousAddress = new Uri("https://previous-media.invalid/file");
        var directory = CreateTemporaryDirectory("range-overlap-later-validator-mismatch");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            await WritePartialAsync(
                target,
                payload,
                ResourceIdentity("\"resource-v1\"", previousAddress));
            using var handler = new RangePayloadHandler(
                payload,
                chunkEntityTag: "\"resource-v2\"",
                overlapEntityTag: "\"resource-v1\"");
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));

            Assert.Equal([(0L, 3L), (4L, 7L)], handler.ChunkRanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BodyWithoutDataTriggersBoundedTransientFailure()
    {
        var payload = CreatePayload(4);
        using var handler = new RangePayloadHandler(payload, stallAtStart: 0);
        var directory = CreateTemporaryDirectory("range-stall");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4,
                readStallTimeout: TimeSpan.FromMilliseconds(50));

            var error = await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));
            var classified = BuiltinTransferBackend.ClassifyFailure(
                error,
                reportedCanceled: false);

            Assert.Equal(DownloadTransferFailureKind.TransientNetwork, classified.FailureKind);
            Assert.True(File.Exists($"{target}.download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResponseHeadersWithoutDataTriggerBoundedTransientFailure(
        int stalledRequest)
    {
        var payload = CreatePayload(4);
        using var handler = new RangePayloadHandler(
            payload,
            stallBeforeHeadersAtRequest: stalledRequest);
        var directory = CreateTemporaryDirectory("range-header-stall");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4,
                readStallTimeout: TimeSpan.FromMilliseconds(50));

            var error = await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(
                target,
                payload.Length,
                TestContext.Current.CancellationToken));
            var classified = BuiltinTransferBackend.ClassifyFailure(
                error,
                reportedCanceled: false);

            Assert.Equal(DownloadTransferFailureKind.TransientNetwork, classified.FailureKind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingResponseLengthIsAProtocolFailureInsteadOfADiskFailure()
    {
        using var handler = new UnknownLengthHandler();
        var directory = CreateTemporaryDirectory("range-missing-length");
        var target = Path.Combine(directory, "media.bin");
        try
        {
            using var downloader = CreateDownloader(
                handler,
                parallelCount: 1,
                segmentSize: 4);

            var error = await Assert.ThrowsAsync<HttpIOException>(() => downloader.DownloadAsync(
                target,
                expectedBytes: 0,
                cancellationToken: TestContext.Current.CancellationToken));
            var classified = BuiltinTransferBackend.ClassifyFailure(
                error,
                reportedCanceled: false);

            Assert.Equal(DownloadTransferFailureKind.TransientNetwork, classified.FailureKind);
            Assert.Equal(HttpRequestError.InvalidResponse, error.HttpRequestError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static BuiltinRangeDownloader CreateDownloader(
        HttpMessageHandler handler,
        int parallelCount,
        long segmentSize,
        TimeSpan? readStallTimeout = null,
        Uri? address = null,
        DownloadBandwidthLimiter.TransferLease? bandwidthLimiter = null,
        Action<int>? planCreated = null)
    {
        return new BuiltinRangeDownloader(
            handler,
            address ?? MediaAddress,
            new DownloadRequestHeaders(
                ["Origin: https://www.bilibili.com", "Referer: https://www.bilibili.com"],
                "DownKyi-Test",
                CarriesCredentials: false),
            parallelCount,
            static (_, _) => { },
            NullLogger.Instance,
            segmentSize,
            readStallTimeout,
            disposeHandler: false,
            bandwidthLimiter: bandwidthLimiter,
            planCreated: planCreated);
    }

    private static byte[] CreatePayload(int length) =>
        Enumerable.Range(0, length).Select(static value => checked((byte)value)).ToArray();

    private sealed class AdvancingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            _timestamp += elapsed.Ticks;
        }
    }

    private static readonly Uri MediaAddress = new("https://media.invalid/file");

    private static object ResourceIdentity(string? strongETag, Uri? address = null) =>
        new
        {
            AddressHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes((address ?? MediaAddress).AbsoluteUri))),
            StrongETag = strongETag,
            LastModified = (DateTimeOffset?)null
        };

    private static async Task WritePartialAsync(
        string target,
        byte[] payload,
        object? resourceIdentity,
        int secondChunkPosition = 0,
        int thirdChunkPosition = 0)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            TotalFileSize = payload.Length,
            Chunks = new[]
            {
                new { Start = 0, End = 3, Position = 4 },
                new { Start = 4, End = 7, Position = secondChunkPosition },
                new { Start = 8, End = 11, Position = thirdChunkPosition }
            },
            ResourceIdentity = resourceIdentity
        });
        var stream = new FileStream(
            $"{target}.download",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using (var streamLifetime = stream.ConfigureAwait(false))
        {
            stream.SetLength(payload.Length);
            await stream.WriteAsync(
                payload.AsMemory(0, 4 + secondChunkPosition),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            if (thirdChunkPosition > 0)
            {
                stream.Position = 8;
                await stream.WriteAsync(
                    payload.AsMemory(8, thirdChunkPosition),
                    TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            stream.Position = payload.Length;
            await stream.WriteAsync(
                metadata,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteSingleChunkPartialAsync(
        string target,
        byte[] payload,
        object? resourceIdentity,
        int retainedLength)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            TotalFileSize = payload.Length,
            Chunks = new[]
            {
                new { Start = 0, End = payload.Length - 1, Position = retainedLength }
            },
            ResourceIdentity = resourceIdentity
        });
        var stream = new FileStream(
            $"{target}.download",
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        await using (var streamLifetime = stream.ConfigureAwait(false))
        {
            stream.SetLength(payload.Length);
            await stream.WriteAsync(
                payload.AsMemory(0, retainedLength),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            stream.Position = payload.Length;
            await stream.WriteAsync(
                metadata,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    private static string CreateTemporaryDirectory(string purpose)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"downkyi-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RangePayloadHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;
        private readonly int? _failOnceAtStart;
        private readonly int _failureBytes;
        private readonly ConcurrentQueue<(long Start, long End)> _chunkRanges = new();
        private readonly ConcurrentQueue<string?> _ifRangeValues = new();
        private readonly ConcurrentReadGate? _readGate;
        private readonly int? _stallAtStart;
        private readonly int? _stallBeforeHeadersAtRequest;
        private readonly string? _entityTag;
        private readonly string? _chunkEntityTag;
        private readonly string? _overlapEntityTag;
        private readonly bool _omitChunkValidators;
        private readonly DateTimeOffset? _lastModified;
        private readonly DateTimeOffset? _responseDate;
        private int _failureReturned;
        private int _requests;

        public RangePayloadHandler(
            byte[] payload,
            int requiredConcurrentReads = 0,
            int? failOnceAtStart = null,
            int failureBytes = 0,
            int? stallAtStart = null,
            int? stallBeforeHeadersAtRequest = null,
            string? entityTag = "\"resource-v1\"",
            string? chunkEntityTag = null,
            string? overlapEntityTag = null,
            bool omitChunkValidators = false,
            DateTimeOffset? lastModified = null,
            DateTimeOffset? responseDate = null)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            _failOnceAtStart = failOnceAtStart;
            _failureBytes = failureBytes;
            _stallAtStart = stallAtStart;
            _stallBeforeHeadersAtRequest = stallBeforeHeadersAtRequest;
            _entityTag = entityTag;
            _chunkEntityTag = chunkEntityTag;
            _overlapEntityTag = overlapEntityTag;
            _omitChunkValidators = omitChunkValidators;
            _lastModified = lastModified;
            _responseDate = responseDate;
            if (requiredConcurrentReads > 0)
            {
                _readGate = new ConcurrentReadGate(requiredConcurrentReads);
            }
        }

        public IReadOnlyCollection<(long Start, long End)> ChunkRanges =>
            _chunkRanges.ToArray();

        public IReadOnlyCollection<string?> IfRangeValues => _ifRangeValues.ToArray();

        public int MaximumConcurrentReads => _readGate?.MaximumConcurrentReads ?? 0;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _requests) == _stallBeforeHeadersAtRequest)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }

            var range = Assert.Single(request.Headers.Range?.Ranges ?? []);
            var start = range.From ?? 0;
            var end = range.To ?? (_payload.LongLength - 1);
            var isProbe = start == 0 && end == 0;
            if (!isProbe)
            {
                _chunkRanges.Enqueue((start, end));
                _ifRangeValues.Enqueue(
                    request.Headers.TryGetValues("If-Range", out var values)
                        ? values.Single()
                        : null);
            }

            Stream contentStream;
            var declaredLength = checked((int)(end - start + 1));
            if (!isProbe && _stallAtStart == start)
            {
                contentStream = new StallingStream();
            }
            else
            {
                var actualLength = declaredLength;
                if (!isProbe
                    && _failOnceAtStart == start
                    && Interlocked.Exchange(ref _failureReturned, 1) == 0)
                {
                    actualLength = Math.Min(_failureBytes, declaredLength);
                }

                var body = _payload.AsMemory(checked((int)start), actualLength).ToArray();
                contentStream = new MemoryStream(body, writable: false);
                if (!isProbe && _readGate != null)
                {
                    contentStream = new GatedReadStream(contentStream, _readGate);
                }
            }

            var content = new StreamContent(contentStream);
            content.Headers.ContentLength = declaredLength;
            content.Headers.ContentRange = new ContentRangeHeaderValue(
                start,
                end,
                _payload.LongLength);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content,
                RequestMessage = request
            };
            var entityTag = GetEntityTag(isProbe, start);
            if (entityTag != null)
            {
                response.Headers.ETag = EntityTagHeaderValue.Parse(entityTag);
            }
            if (_lastModified != null && (isProbe || !_omitChunkValidators))
            {
                response.Content.Headers.LastModified = _lastModified;
            }
            if (_responseDate != null)
            {
                response.Headers.Date = _responseDate;
            }

            return response;
        }

        private string? GetEntityTag(bool isProbe, long start)
        {
            if (isProbe)
            {
                return _entityTag;
            }

            if (start == 0 && _overlapEntityTag != null)
            {
                return _overlapEntityTag;
            }

            return _omitChunkValidators ? null : _chunkEntityTag ?? _entityTag;
        }
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("The completed target must not be downloaded again."));
        }
    }

    private sealed class NonRangePayloadHandler(byte[] payload) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = payload.LongLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private sealed class UnknownLengthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnknownLengthContent(),
                RequestMessage = request
            });
        }
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ConcurrentReadGate(int requiredConcurrentReads)
    {
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeReads;
        private int _maximumConcurrentReads;

        public int MaximumConcurrentReads => Volatile.Read(ref _maximumConcurrentReads);

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeReads);
            UpdateMaximum(active);
            if (active >= requiredConcurrentReads)
            {
                _ready.TrySetResult();
            }

            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Exit() => Interlocked.Decrement(ref _activeReads);

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentReads);
                if (active <= current
                    || Interlocked.CompareExchange(
                        ref _maximumConcurrentReads,
                        active,
                        current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class GatedReadStream(Stream inner, ConcurrentReadGate gate) : Stream
    {
        private int _entered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _entered, 1) == 0)
            {
                await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                if (Interlocked.Exchange(ref _entered, 0) != 0)
                {
                    gate.Exit();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
