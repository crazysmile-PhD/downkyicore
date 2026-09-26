using DownKyi.Application.Lifetime;
using DownKyi.Desktop.Composition;
using DownKyi.Services.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DownKyi.Tests;

public sealed class ContentDownloadBatchOwnerTests
{
    [Fact]
    public async Task SubmittedBatchesRunInFifoOrderWithoutOverlap()
    {
        using var applicationCancellation = new ApplicationCancellation();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new List<string>();
        var activeCount = 0;
        var maximumActiveCount = 0;
        using var owner = CreateOwner(
            applicationCancellation,
            async (items, _, cancellationToken) =>
            {
                var currentActiveCount = Interlocked.Increment(ref activeCount);
                maximumActiveCount = Math.Max(maximumActiveCount, currentActiveCount);
                var source = Assert.Single(items).Source;
                executionOrder.Add(source);
                try
                {
                    if (source == "first")
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        secondStarted.TrySetResult();
                    }

                    return 1;
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            });
        await owner.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var first = owner.AddAsync(
                [CreateItem("first")],
                onlySelected: false,
                TestContext.Current.CancellationToken);
            await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = owner.AddAsync(
                [CreateItem("second")],
                onlySelected: false,
                TestContext.Current.CancellationToken);

            Assert.False(secondStarted.Task.IsCompleted);
            releaseFirst.TrySetResult();

            Assert.Equal(1, await first.ConfigureAwait(true));
            Assert.Equal(1, await second.ConfigureAwait(true));
            Assert.Equal(["first", "second"], executionOrder);
            Assert.Equal(1, maximumActiveCount);
        }
        finally
        {
            releaseFirst.TrySetResult();
            await owner.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExplicitCancellationSkipsAQueuedBatchWithoutCancelingTheRunningBatch()
    {
        using var applicationCancellation = new ApplicationCancellation();
        using var queuedCancellation = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executedSources = new List<string>();
        using var owner = CreateOwner(
            applicationCancellation,
            async (items, _, cancellationToken) =>
            {
                var source = Assert.Single(items).Source;
                executedSources.Add(source);
                if (source == "first")
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return 1;
            });
        await owner.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var first = owner.AddAsync(
                [CreateItem("first")],
                onlySelected: false,
                TestContext.Current.CancellationToken);
            await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var queued = owner.AddAsync(
                [CreateItem("queued")],
                onlySelected: false,
                queuedCancellation.Token);

            await queuedCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(["first"], executedSources);

            releaseFirst.TrySetResult();
            Assert.Equal(1, await first.ConfigureAwait(true));
        }
        finally
        {
            releaseFirst.TrySetResult();
            await owner.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ExplicitCancellationStopsTheRunningBatch()
    {
        using var applicationCancellation = new ApplicationCancellation();
        using var operationCancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = CreateOwner(
            applicationCancellation,
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 1;
            });
        await owner.StartAsync(TestContext.Current.CancellationToken);

        var batch = owner.AddAsync(
            [CreateItem("running")],
            onlySelected: false,
            operationCancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await operationCancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        await owner.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopCancelsAndAwaitsTheRunningBatch()
    {
        using var applicationCancellation = new ApplicationCancellation();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner = CreateOwner(
            applicationCancellation,
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await allowCleanup.Task.ConfigureAwait(false);
                    throw;
                }
            });
        await owner.StartAsync(TestContext.Current.CancellationToken);

        var batch = owner.AddAsync(
            [CreateItem("running")],
            onlySelected: false,
            CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = owner.StopAsync(TestContext.Current.CancellationToken);

        try
        {
            await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(applicationCancellation.ShutdownToken.IsCancellationRequested);
            Assert.False(stop.IsCompleted);

            allowCleanup.TrySetResult();
            await stop.ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        }
        finally
        {
            allowCleanup.TrySetResult();
        }
    }

    [Fact]
    public async Task GenericHostStopCancelsAndAwaitsTheRunningBatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new StubContentDownloadCoordinator(
            async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                    return 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await allowCleanup.Task.ConfigureAwait(false);
                    throw;
                }
            });
        using var host = DownKyiHost.Create(services =>
        {
            services.AddSingleton<ContentDownloadBatchOwner>(provider =>
                new ContentDownloadBatchOwner(
                    coordinator,
                    provider.GetRequiredService<ApplicationCancellation>()));
            services.AddSingleton<IHostedService>(provider =>
                provider.GetRequiredService<ContentDownloadBatchOwner>());
        });
        await host.StartAsync(TestContext.Current.CancellationToken);
        var owner = host.Services.GetRequiredService<ContentDownloadBatchOwner>();
        var batch = owner.AddAsync(
            [CreateItem("running")],
            onlySelected: false,
            CancellationToken.None);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stop = host.StopAsync(TestContext.Current.CancellationToken);

        try
        {
            await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(host.Services
                .GetRequiredService<ApplicationCancellation>()
                .ShutdownToken
                .IsCancellationRequested);
            Assert.False(stop.IsCompleted);

            allowCleanup.TrySetResult();
            await stop.ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch);
        }
        finally
        {
            allowCleanup.TrySetResult();
            await stop.ConfigureAwait(true);
        }
    }

    private static ContentDownloadBatchOwner CreateOwner(
        ApplicationCancellation applicationCancellation,
        Func<IReadOnlyList<ContentDownloadItem>, bool, CancellationToken, Task<int?>> executeAsync) =>
        new(new StubContentDownloadCoordinator(executeAsync), applicationCancellation);

    private static ContentDownloadItem CreateItem(string source) =>
        new(source, DownloadInfoKind.Video, IsSelected: true);

    private sealed class StubContentDownloadCoordinator(
        Func<IReadOnlyList<ContentDownloadItem>, bool, CancellationToken, Task<int?>> executeAsync)
        : IContentDownloadCoordinator
    {
        public Task<int?> AddAsync(
            IReadOnlyList<ContentDownloadItem> items,
            bool onlySelected,
            CancellationToken cancellationToken) =>
            executeAsync(items, onlySelected, cancellationToken);
    }
}
