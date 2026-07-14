using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using Prism.Dialogs;

namespace DownKyi.Tests;

public sealed class LegacyUpgradeViewModelTests
{
    [Fact]
    public async Task ClosingDialogCancelsActiveMigration()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        using var viewModel = new ViewUpgradingDialogViewModel(coordinator, new DownloadListState());

        viewModel.OnDialogOpened(new DialogParameters());
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        viewModel.OnDialogClosed();

        await coordinator.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public void CompletedMigrationReplacesDownloadedProjection()
    {
        var item = new DownloadedItem
        {
            DownloadBase = new DownloadBase { Id = "migrated", MainTitle = "Migrated" },
            Downloaded = new Downloaded { Id = "migrated" }
        };
        var state = new DownloadListState();
        using var viewModel = new ViewUpgradingDialogViewModel(
            new CompletedLegacyUpgradeCoordinator(item),
            state);

        viewModel.OnDialogOpened(new DialogParameters());

        Assert.Same(item, Assert.Single(state.Downloaded));
        Assert.Equal(100, viewModel.Percent);
        Assert.True(viewModel.RestartVisible);
    }

    private sealed class BlockingLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                Canceled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The blocking migration unexpectedly completed.");
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
}
