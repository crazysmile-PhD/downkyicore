using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class BuiltinRangeSpeculationTests
{
    [Fact]
    public async Task CancellationDrainsBothRangesBeforeReleasingTheReservation()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-hedge-cancel-").FullName;
        var clock = new TestClock();
        var policy = new BuiltinRangeSchedulingPolicy(clock, TimeSpan.FromMilliseconds(20));
        using var handler = new SpeculativeHandler(bothStall: true,
            primaryStalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, static (_, _) => { }, NullLogger.Instance, disposeHandler: false, schedulingPolicy: policy);
            var transfer = downloader.DownloadAsync(Path.Combine(directory, "media"), handler.Payload.Length, lifetime.Token);
            await handler.BackupReturned.Task.WaitAsync(TestContext.Current.CancellationToken);
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
            Assert.True(handler.PrimaryStopped);
            Assert.True(handler.BackupStopped);
            // Cancellation joins the sole observer before the final checkpoint.
            // Inspect the wire format independently of its reader.
            var partial = await File.ReadAllBytesAsync(
                Path.Combine(directory, "media.download"), TestContext.Current.CancellationToken);
            using var checkpoint = JsonDocument.Parse(partial.AsMemory(handler.Payload.Length));
            Assert.Equal(handler.Payload.Length, checkpoint.RootElement.GetProperty("TotalFileSize").GetInt64());
            Assert.Equal(16, Assert.Single(checkpoint.RootElement.GetProperty("Chunks").EnumerateArray())
                .GetProperty("Position").GetInt64());
            using var exclusive = new FileStream(Path.Combine(directory, "media.download"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            clock.Advance(TimeSpan.FromSeconds(1));
            var sample = new BuiltinRangeSample(new BuiltinRangeChunk(0, 31, 0), 32,
                true, true, 0, 0, TimeSpan.FromSeconds(20), false, TimeSpan.Zero);
            var next = Assert.IsType<BuiltinRangeHedge>(policy.TryReserve([sample], limited: false));
            policy.Complete(next, won: false);
            Assert.False(File.Exists(Path.Combine(directory, "media")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BackupBodyReadsConsumeTheBudgetSharedWithLaterAttempts()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-hedge-budget-").FullName;
        var clock = new TestClock();
        var policy = new BuiltinRangeSchedulingPolicy(clock, TimeSpan.FromMilliseconds(20));
        using var handler = new SpeculativeHandler(primaryStalled: () => clock.Advance(TimeSpan.FromSeconds(20)));
        try
        {
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, static (_, _) => { }, NullLogger.Instance, disposeHandler: false, schedulingPolicy: policy);
            var result = await downloader.DownloadAsync(Path.Combine(directory, "media"), handler.Payload.Length,
                TestContext.Current.CancellationToken);
            Assert.Equal(handler.Payload.Length, result.ReceivedBytes);
            Assert.Equal(2, handler.RangeRequests);
            // The backup read 4080 bytes of a 4096-byte allowance. Completing
            // the file or initializing another attempt must not refill it.
            clock.Advance(TimeSpan.FromSeconds(1));
            policy.InitializeBudget(100 * 1024 * 1024);
            var candidate = new BuiltinRangeSample(new BuiltinRangeChunk(0, 31, 0), 32,
                true, true, 0, 0, TimeSpan.FromSeconds(20), false, TimeSpan.Zero);
            Assert.Null(policy.TryReserve([candidate], limited: false));
            Assert.Equal(handler.Payload, await File.ReadAllBytesAsync(Path.Combine(directory, "media"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrimaryTransportFailureDoesNotCancelAValidBackup()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-primary-fails-").FullName;
        var target = Path.Combine(directory, "media");
        using var handler = new SpeculativeHandler(primaryFails: true);
        try
        {
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, static (_, _) => { }, NullLogger.Instance,
                disposeHandler: false, schedulingPolicy: new BuiltinRangeSchedulingPolicy(TimeProvider.System, TimeSpan.FromMilliseconds(20)));
            var result = await downloader.DownloadAsync(target, handler.Payload.Length,
                TestContext.Current.CancellationToken);
            Assert.Equal(handler.Payload.Length, result.ReceivedBytes);
            Assert.Equal(2, handler.RangeRequests);
            Assert.True(handler.PrimaryStopped);
            Assert.Equal(handler.Payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrimaryWinningTheRaceDrainsItsSpeculativeLoser()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-primary-wins-").FullName;
        var target = Path.Combine(directory, "media");
        using var handler = new SpeculativeHandler(primaryWins: true);
        try
        {
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, static (_, _) => { }, NullLogger.Instance,
                disposeHandler: false, schedulingPolicy: new BuiltinRangeSchedulingPolicy(TimeProvider.System, TimeSpan.FromMilliseconds(20)));
            var result = await downloader.DownloadAsync(target, handler.Payload.Length,
                TestContext.Current.CancellationToken);
            Assert.Equal(handler.Payload.Length, result.ReceivedBytes);
            Assert.Equal(2, handler.RangeRequests);
            Assert.True(handler.BackupStopped);
            Assert.Equal(handler.Payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FasterBackupRangeReplacesOnlyMissingBytesAfterPrimaryStops()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-hedge-").FullName;
        var target = Path.Combine(directory, "media");
        using var handler = new SpeculativeHandler();
        try
        {
            var progress = new List<long>();
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, (received, _) => progress.Add(received), NullLogger.Instance,
                disposeHandler: false, schedulingPolicy: new BuiltinRangeSchedulingPolicy(TimeProvider.System, TimeSpan.FromMilliseconds(20)));
            var result = await downloader.DownloadAsync(target, handler.Payload.Length,
                TestContext.Current.CancellationToken);

            Assert.True(handler.PrimaryStopped);
            Assert.Equal(2, handler.RangeRequests);
            Assert.Equal(16, handler.BackupStart);
            Assert.Equal(handler.Payload, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.Equal(handler.Payload.Length, result.ReceivedBytes);
            Assert.All(progress, bytes => Assert.InRange(bytes, 0, handler.Payload.Length));
            Assert.False(File.Exists(target + ".download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MismatchingSpeculativeVersionCannotPublishAFile()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-hedge-mismatch-").FullName;
        var target = Path.Combine(directory, "media");
        using var handler = new SpeculativeHandler(mismatch: true);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            using var downloader = new BuiltinRangeDownloader(handler,
                new Uri("https://media.invalid/file"), new DownloadRequestHeaders([], "test", false),
                1, static (_, _) => { }, NullLogger.Instance,
                disposeHandler: false, schedulingPolicy: new BuiltinRangeSchedulingPolicy(TimeProvider.System, TimeSpan.FromMilliseconds(20)));
            var transfer = downloader.DownloadAsync(target, handler.Payload.Length, lifetime.Token);
            await handler.BackupReturned.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<BuiltinResumeRejectedException>(() => transfer);
            Assert.True(handler.PrimaryStopped);
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(target + ".download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SpeculativeHandler(bool mismatch = false, bool primaryWins = false, bool primaryFails = false,
        Action? primaryStalled = null, bool bothStall = false) : HttpMessageHandler
    {
        public byte[] Payload { get; } = Enumerable.Range(0, 4096).Select(static value => (byte)(value % 251)).ToArray();
        public int RangeRequests { get; private set; }
        public bool PrimaryStopped { get; private set; }
        public bool BackupStopped { get; private set; }
        public long BackupStart { get; private set; }
        public TaskCompletionSource BackupReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource PrimaryFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = range.From!.Value;
            var end = range.To!.Value;
            var probe = start == 0 && end == 0;
            var ordinal = probe ? 0 : ++RangeRequests;
            Stream stream = ordinal switch
            {
                1 => new PrefixThenStallStream(Payload, () =>
                {
                    PrimaryStopped = true;
                    PrimaryFinished.TrySetResult();
                }, primaryWins || primaryFails ? BackupReturned.Task : null, primaryFails, primaryStalled),
                2 when primaryFails => new WaitingStream(Payload[(int)start..((int)end + 1)], PrimaryFinished.Task),
                2 when primaryWins || bothStall => new PrefixThenStallStream(Payload[(int)start..((int)end + 1)],
                    () => BackupStopped = true),
                _ => new MemoryStream(Payload[(int)start..((int)end + 1)])
            };
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(stream),
                RequestMessage = request
            };
            response.Content.Headers.ContentLength = end - start + 1;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Payload.Length);
            response.Headers.ETag = new EntityTagHeaderValue(mismatch && ordinal == 2 ? "\"changed\"" : "\"same\"");
            if (ordinal == 2)
            {
                Assert.Equal("\"same\"", request.Headers.IfRange?.ToString());
                BackupStart = start;
                BackupReturned.TrySetResult();
            }

            return Task.FromResult(response);
        }
    }

    private sealed class WaitingStream(byte[] payload, Task release) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PrefixThenStallStream(byte[] payload, Action stopped, Task? release = null,
        bool fail = false, Action? stalled = null) : MemoryStream(payload)
    {
        private bool _prefixRead;
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stopped();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefixRead)
            {
                _prefixRead = true;
                return await base.ReadAsync(buffer[..Math.Min(16, buffer.Length)], cancellationToken).ConfigureAwait(false);
            }

            try
            {
                stalled?.Invoke();
                if (release != null)
                {
                    await release.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (fail)
                    {
                        throw new HttpIOException(HttpRequestError.ResponseEnded, "Primary disconnected.");
                    }

                    return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                stopped();
            }
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
