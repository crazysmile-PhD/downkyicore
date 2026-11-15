using System.Threading.Channels;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadTransferSessionTests
{
    [Fact]
    public async Task DrippingSourceIsRevokedAndDrainedBeforeBackupStarts()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-session-").FullName;
        var clock = new TestClock();
        var firstStopped = false;
        DownloadTransferRequest? expiredLease = null;
        var updates = new List<long?>();
        var calls = new List<string>();
        var drippingBytes = 1000L;
        try
        {
            var backend = new Backend(async request =>
            {
                calls.Add(request.Urls[0]);
                if (calls.Count == 1)
                {
                    expiredLease = request;
                    await File.WriteAllTextAsync(Path.Combine(directory, "media.download"), "old-resource",
                        request.CancellationToken).ConfigureAwait(false);
                    request.PublishProgress(new DownloadProgress(0, 1000, 100000));
                    request.NetworkReadStateChanged!(true);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        request.NetworkReadStateChanged(false);
                        request.PublishProgress(new DownloadProgress(50, 50000, 100000));
                        firstStopped = true;
                    }
                }

                Assert.True(firstStopped);
                Assert.False(File.Exists(Path.Combine(directory, "media.download")));
                request.PublishProgress(new DownloadProgress(100, 4, 4));
                return DownloadTransferResult.Succeeded();
            });
            var request = Request(directory) with
            {
                EnsureActive = () =>
                {
                    clock.Advance(TimeSpan.FromSeconds(20));
                    expiredLease?.PublishProgress(new DownloadProgress(1,
                        Interlocked.Add(ref drippingBytes, 1000), 100000));
                },
                PublishProgress = progress => updates.Add(progress.DownloadedBytes)
            };
            var transfer = Coordinator(backend, clock).TransferAsync(request,
                static _ => throw new InvalidOperationException("Backup must finish without refresh."),
                TestContext.Current.CancellationToken);
            Assert.Equal(DownloadTransferOutcome.Succeeded, (await transfer.ConfigureAwait(true)).Outcome);
            expiredLease!.PublishProgress(new DownloadProgress(75, 75000, 100000));
            Assert.DoesNotContain(50000L, updates);
            Assert.DoesNotContain(75000L, updates);
            Assert.Equal(4, updates[^1]);
            Assert.Equal(["https://primary.invalid/media", "https://backup.invalid/media"], calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task UserLimitAndLocalDiskWaitDoNotSuspectTheSource(bool limited, bool networkWaiting)
    {
        var clock = new TestClock();
        var ticks = Channel.CreateUnbounded<bool>();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend(async request =>
        {
            request.PublishProgress(new DownloadProgress(0, 0, 100));
            request.NetworkReadStateChanged!(networkWaiting);
            await finish.Task.WaitAsync(request.CancellationToken).ConfigureAwait(false);
            return DownloadTransferResult.Succeeded();
        });
        var request = Request(Path.GetTempPath()) with { EnsureActive = () => ticks.Writer.TryWrite(true) };
        var transfer = Coordinator(backend, clock, limited).TransferAsync(request,
            static _ => throw new InvalidOperationException("No refresh expected."), TestContext.Current.CancellationToken);
        await TickAsync(ticks);
        for (var index = 0; index < 5; index++)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await TickAsync(ticks);
        }

        Assert.False(transfer.IsCompleted);
        finish.SetResult();
        Assert.Equal(DownloadTransferOutcome.Succeeded, (await transfer.ConfigureAwait(true)).Outcome);
    }

    [Fact]
    public async Task EnsureActiveFailureStillDrainsWorkerAndDoesNotPersistStaleProgress()
    {
        var stopped = false;
        var persisted = false;
        var backend = new Backend(async request =>
        {
            request.PublishProgress(new DownloadProgress(10, 10, 100));
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken).ConfigureAwait(false);
                return DownloadTransferResult.Succeeded();
            }
            finally
            {
                stopped = true;
            }
        });
        var request = Request(Path.GetTempPath()) with
        {
            EnsureActive = static () => throw new InvalidOperationException("Task already canceled."),
            PersistProgressAsync = (_, _) => { persisted = true; return Task.CompletedTask; }
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Coordinator(backend, new TestClock()).TransferAsync(request,
                static _ => Task.FromResult<IReadOnlyList<string>>([]), TestContext.Current.CancellationToken));
        Assert.Equal("Task already canceled.", exception.Message);
        Assert.True(stopped);
        Assert.False(persisted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PauseAndCallerCancellationJoinWorkerBeforeReturning(bool pause)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopped = false;
        var paused = false;
        var backend = new Backend(async request =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, request.CancellationToken).ConfigureAwait(false);
                return DownloadTransferResult.Succeeded();
            }
            finally
            {
                stopped = true;
            }
        });
        var request = Request(Path.GetTempPath()) with { IsPauseRequested = () => paused };
        var transfer = Coordinator(backend, new TestClock()).TransferAsync(request,
            static _ => throw new InvalidOperationException("No refresh after cancellation."), lifetime.Token);
        await started.Task.ConfigureAwait(true);
        if (pause)
        {
            paused = true;
            Assert.Equal(DownloadTransferOutcome.Paused, (await transfer.ConfigureAwait(true)).Outcome);
        }
        else
        {
            await lifetime.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
        }

        Assert.True(stopped);
    }

    [Fact]
    public async Task FreshTransferChoosesFastestSurveyedReplicaButResumeKeepsAffinity()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-survey-").FullName;
        try
        {
            var backend = new SurveyBackend();
            var coordinator = Coordinator(backend, new TestClock());
            var request = Request(directory);
            await coordinator.TransferAsync(request, static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);
            Assert.Equal("https://backup.invalid/media", backend.Chosen);
            Assert.Equal(2, backend.Probes);
            await File.WriteAllTextAsync(Path.Combine(directory, "media.download"), "checkpoint",
                TestContext.Current.CancellationToken);
            await coordinator.TransferAsync(request, static _ => Task.FromResult<IReadOnlyList<string>>([]),
                TestContext.Current.CancellationToken);
            Assert.Equal("https://primary.invalid/media", backend.Chosen);
            Assert.Equal(2, backend.Probes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task TickAsync(Channel<bool> ticks) =>
        _ = await ticks.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

    private static DownloadTransferCoordinator Coordinator(ITransferBackend backend, TimeProvider clock, bool limited = false) =>
        new(backend, new DownloadRetryPolicy(baseDelay: TimeSpan.Zero), clock,
            NullLogger<DownloadTransferCoordinator>.Instance, () => limited,
            new DownloadTransferWatchdog(TimeSpan.FromMilliseconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(10), 16384, 2));

    private static DownloadTransferRequest Request(string directory) => new(
        new DownloadTaskId("session"), ["https://primary.invalid/media", "https://backup.invalid/media"],
        directory, "media", 0, static () => { }, static () => false, static _ => { },
        static (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken, directory);

    private sealed class Backend(Func<DownloadTransferRequest, Task<DownloadTransferResult>> run) : ITransferBackend
    {
        public string Name => "session-test";
        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request) => run(request);
    }

    private sealed class SurveyBackend : ITransferBackend, IDownloadSourceProbe
    {
        private int _probes;
        public int Probes => _probes;
        public string? Chosen { get; private set; }
        public string Name => "survey-test";
        public Task<DownloadSourceObservation> ProbeAsync(string address, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            return Task.FromResult<DownloadSourceObservation>(new DownloadSourceReady(
                TimeSpan.FromSeconds(address.Contains("primary", StringComparison.Ordinal) ? 2 : 0.1), 65536, true, true));
        }

        public Task<DownloadTransferResult> TransferAsync(DownloadTransferRequest request)
        {
            Chosen = request.Urls[0];
            return Task.FromResult(DownloadTransferResult.Succeeded());
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
