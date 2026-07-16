using DownKyi.Application.Downloads;
using DownKyi.Application.Time;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Platform;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadBootstrapHostedServiceTests
{
    [Fact]
    public async Task HostLifecycleOwnsDownloadRuntimeAndUiProjection()
    {
        using var runtime = new RecordingDownloadRuntime();
        var dispatcher = new ImmediateUiDispatcher();
        var listState = new DownloadListState();
        using var storage = new DownloadStorageService(new EmptyDownloadTaskStore(), new FixedClock());
        using var service = new DownloadBootstrapHostedService(
            listState,
            storage,
            new RecordingRuntimeFactory(runtime),
            dispatcher,
            NullLogger<DownloadBootstrapHostedService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.Started);
        Assert.True(runtime.Ended);
        Assert.True(dispatcher.InvocationCount >= 2);
        Assert.Empty(listState.Downloading);
        Assert.Empty(listState.Downloaded);
    }

    private sealed class RecordingRuntimeFactory(IDownloadRuntime runtime) : IDownloadRuntimeFactory
    {
        public IDownloadRuntime Create()
        {
            return runtime;
        }
    }

    private sealed class RecordingDownloadRuntime : IDownloadRuntime
    {
        public bool Started { get; private set; }

        public bool Ended { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ended = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvocationCount++;
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UnixEpoch;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class EmptyDownloadTaskStore : IDownloadTaskStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<OperationResult> AddAsync(DownloadTask task, CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateAsync(
            DownloadTask task,
            long expectedVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> UpdateProgressAsync(
            DownloadProgressWrite progressWrite,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<DownloadTask?> FindAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            return Task.FromResult<DownloadTask?>(null);
        }

        public Task<IReadOnlyList<DownloadTask>> GetUnfinishedAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<DownloadTask>>(Array.Empty<DownloadTask>());
        }

        public Task<DownloadHistoryPage> GetHistoryPageAsync(
            DownloadHistoryCursor? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new DownloadHistoryPage(Array.Empty<DownloadTask>(), null));
        }

        public Task<OperationResult> DeleteAsync(DownloadTaskId taskId, CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<OperationResult> ClearHistoryAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(OperationResult.Success());
        }

        public Task<IReadOnlyList<QuarantinedDownloadRecord>> GetQuarantinedRecordsAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<QuarantinedDownloadRecord>>(
                Array.Empty<QuarantinedDownloadRecord>());
        }
    }
}
