using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Models;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LegacyUpgradeViewModelTests
{
    [Fact]
    public async Task ClosingDialogCancelsActiveMigration()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        using var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        viewModel.OnDialogClosed();

        await coordinator.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task ActiveMigrationCloseShowsConfirmationAndContinueKeepsMigrationRunning()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        using var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(viewModel.CanCloseDialog());
        Assert.True(viewModel.CancelConfirmationVisible);

        viewModel.ContinueMigrationCommand.Execute(null);

        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.True(viewModel.IsMigrationActive);
        Assert.False(coordinator.Canceled.Task.IsCompleted);

        viewModel.OnDialogClosed();
        await coordinator.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task ConfirmedCancellationStopsMigrationAndClosesDialog()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        using var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);
        AppDialogResult? closeResult = null;
        viewModel.CloseRequested += (_, result) => closeResult = result;

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        viewModel.RequestCancelMigrationCommand.Execute(null);
        viewModel.ConfirmCancelMigrationCommand.Execute(null);

        await coordinator.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(viewModel.IsMigrationActive);
        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.Equal(AppDialogOutcome.Canceled, Assert.IsType<AppDialogResult>(closeResult).Outcome);
    }

    [Fact]
    public async Task ForcedHostCloseBypassesMigrationCloseGuard()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        using var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            new DownloadListState(),
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(AvaloniaDialogService.ShouldCancelClose(
            closeRequested: false,
            forcedCloseRequested: true,
            viewModel));
        Assert.False(viewModel.CancelConfirmationVisible);

        viewModel.OnDialogClosed();
        await coordinator.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task MigrationCompletionDismissesPendingCancelConfirmation()
    {
        var item = new DownloadedItem
        {
            DownloadBase = new DownloadBase { Id = "completed", MainTitle = "Completed" },
            Downloaded = new Downloaded { Id = "completed" }
        };
        var coordinator = new ControllableLegacyUpgradeCoordinator();
        var state = new DownloadListState();
        using var viewModel = new ViewUpgradingDialogViewModel(
            coordinator,
            state,
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        viewModel.RequestCancelMigrationCommand.Execute(null);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewUpgradingDialogViewModel.RestartVisible) &&
                viewModel.RestartVisible)
            {
                completed.TrySetResult();
            }
        };

        coordinator.Complete(item);
        await completed.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.False(viewModel.IsMigrationActive);
        Assert.True(viewModel.RestartVisible);
        Assert.Same(item, Assert.Single(state.Downloaded));
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
            state,
            new StubApplicationLifecycle(),
            NullLogger<ViewUpgradingDialogViewModel>.Instance);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));

        Assert.Same(item, Assert.Single(state.Downloaded));
        Assert.Equal(100, viewModel.Percent);
        Assert.True(viewModel.RestartVisible);
        Assert.False(viewModel.IsMigrationActive);
        Assert.True(viewModel.CanCloseDialog());
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

    private sealed class ControllableLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        private readonly TaskCompletionSource<LegacyUpgradeResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public void Complete(DownloadedItem item)
        {
            _completion.TrySetResult(new LegacyUpgradeResult(
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
