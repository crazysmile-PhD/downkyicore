using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LegacyUpgradeViewModelTests
{
    [Fact]
    public async Task ClosingDialogAwaitsActiveMigrationTermination()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        Assert.Equal("数据迁移中，关闭此窗口将取消迁移", viewModel.Message);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var closeTask = viewModel.OnDialogClosedAsync();
        await coordinator.CancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        try
        {
            Assert.False(closeTask.IsCompleted);
        }
        finally
        {
            coordinator.AllowTermination.TrySetResult();
        }

        await closeTask.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task CompletedMigrationReplacesDownloadedProjection()
    {
        var item = new DownloadedItem
        {
            DownloadBase = new DownloadBase { Id = "migrated", MainTitle = "Migrated" },
            Downloaded = new Downloaded { Id = "migrated" }
        };
        var state = new DownloadListState();
        var viewModel = new ViewUpgradingDialogViewModel(
            new CompletedLegacyUpgradeCoordinator(item),
            state,
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));

        Assert.Same(item, Assert.Single(state.Downloaded));
        Assert.Equal(100, viewModel.Percent);
        Assert.True(viewModel.RestartVisible);

        await viewModel.OnDialogClosedAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ClosingDialogObservesUnexpectedMigrationFailure()
    {
        var coordinator = new FaultingLegacyUpgradeCoordinator();
        var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        coordinator.Fail.TrySetResult();

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => viewModel.OnDialogClosedAsync()).ConfigureAwait(true);

        Assert.Equal("Unexpected migration failure.", exception.Message);
    }

    private sealed class BlockingLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowTermination { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await AllowTermination.Task.ConfigureAwait(false);
                throw;
            }

            throw new InvalidOperationException("The blocking migration unexpectedly completed.");
        }
    }

    private sealed class FaultingLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Fail.Task.ConfigureAwait(false);
            throw new NotSupportedException("Unexpected migration failure.");
        }
    }

    private sealed class CompletedLegacyUpgradeCoordinator(DownloadedItem item) : ILegacyUpgradeCoordinator
    {
        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Completed,
                [item]));
        }
    }

    private sealed class StubApplicationLifecycle : IApplicationLifecycle
    {
        public Task RequestShutdownAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task ExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> RestartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
