using DownKyi.Application.Desktop;
using DownKyi.Platform;
using DownKyi.Services.Migration;
using DownKyi.ViewModels.Dialogs;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class LegacyUpgradeViewModelTests
{
    [Fact]
    public async Task ClosingDialogAwaitsActiveMigrationTermination()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = new UpgradingDialogViewModel(
            coordinator,
            NullLogger<UpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        Assert.Equal("数据迁移中；如需停止，请使用“取消迁移”", viewModel.Message);

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
    public async Task ActiveMigrationCloseShowsConfirmationAndContinueKeepsMigrationRunning()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = CreateViewModel(coordinator);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(viewModel.CanCloseDialog());
        Assert.True(viewModel.CancelConfirmationVisible);

        viewModel.ContinueMigrationCommand.Execute(null);

        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.True(viewModel.IsMigrationActive);
        Assert.False(coordinator.CancellationObserved.Task.IsCompleted);

        await StopMigrationAsync(viewModel, coordinator).ConfigureAwait(true);
    }

    [Fact]
    public async Task ProgressBarRemainsHiddenUntilMigrationReportsDeterminateProgress()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = CreateViewModel(coordinator);
        await using var viewModelScope = viewModel.ConfigureAwait(true);
        var unknownProgressApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var determinateProgressApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UpgradingDialogViewModel.Message)
                && viewModel.Message == "正在讀取舊資料")
            {
                unknownProgressApplied.TrySetResult();
            }

            if (args.PropertyName == nameof(UpgradingDialogViewModel.IsProgressVisible)
                && viewModel.IsProgressVisible)
            {
                determinateProgressApplied.TrySetResult();
            }
        };

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(viewModel.IsProgressVisible);
        coordinator.ReportProgress(new LegacyUpgradeProgress("正在讀取舊資料"));
        await unknownProgressApplied.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.False(viewModel.IsProgressVisible);

        coordinator.ReportProgress(new LegacyUpgradeProgress("正在遷移下載記錄（37/100）", 37));
        await determinateProgressApplied.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(37, viewModel.Percent);
        Assert.True(viewModel.IsProgressVisible);

        await StopMigrationAsync(viewModel, coordinator).ConfigureAwait(true);
    }

    [Fact]
    public async Task ConfirmedCancellationAwaitsTerminationBeforeRequestingClose()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = CreateViewModel(coordinator);
        await using var viewModelScope = viewModel.ConfigureAwait(true);
        var confirmCancelCommand = viewModel.ConfirmCancelMigrationCommand;
        var commandEnabled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        confirmCancelCommand.CanExecuteChanged += (_, _) =>
        {
            if (confirmCancelCommand.CanExecute(null))
            {
                commandEnabled.TrySetResult();
            }
        };
        var closed = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, result) => closed.TrySetResult(result);

        Assert.False(confirmCancelCommand.CanExecute(null));
        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await commandEnabled.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.True(confirmCancelCommand.CanExecute(null));
        viewModel.RequestCancelMigrationCommand.Execute(null);
        confirmCancelCommand.Execute(null);

        await coordinator.CancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.False(closed.Task.IsCompleted);

        coordinator.AllowTermination.TrySetResult();
        var result = await closed.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(AppDialogOutcome.Canceled, result.Outcome);
        Assert.False(viewModel.IsMigrationActive);
        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.False(confirmCancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ForcedHostCloseBypassesMigrationCloseGuard()
    {
        var coordinator = new BlockingLegacyUpgradeCoordinator();
        var viewModel = CreateViewModel(coordinator);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(AvaloniaDialogService.ShouldCancelClose(
            closeRequested: false,
            forcedCloseRequested: true,
            viewModel));
        Assert.False(viewModel.CancelConfirmationVisible);

        await StopMigrationAsync(viewModel, coordinator).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(true, AppDialogOutcome.Accepted)]
    [InlineData(false, AppDialogOutcome.Canceled)]
    public async Task MigrationFinishesWithoutRequiringRestart(
        bool hasMigration,
        AppDialogOutcome expectedDialogOutcome)
    {
        var viewModel = new UpgradingDialogViewModel(
            new CompletedLegacyUpgradeCoordinator(hasMigration ? LegacyUpgradeOutcome.Completed : LegacyUpgradeOutcome.NoMigration),
            NullLogger<UpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);
        var closed = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, result) => closed.TrySetResult(result);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        var result = await closed.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(expectedDialogOutcome, result.Outcome);
        Assert.False(viewModel.IsMigrationActive);
        Assert.True(viewModel.CanCloseDialog());
        Assert.Empty(result.Parameters);

        await viewModel.OnDialogClosedAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task ExpectedMigrationFailureStopsLoadingAndReturnsDiagnostic()
    {
        var failure = new InvalidOperationException("Expected migration failure.");
        var viewModel = CreateViewModel(new FailedLegacyUpgradeCoordinator(failure));
        await using var viewModelScope = viewModel.ConfigureAwait(true);
        var closed = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, result) => closed.TrySetResult(result);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        var result = await closed.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.False(viewModel.IsMigrationActive);
        Assert.False(viewModel.CancelConfirmationVisible);
        Assert.True(viewModel.CanCloseDialog());
        Assert.Equal("legacy backup preserved", viewModel.Message);
        Assert.Equal(AppDialogOutcome.Rejected, result.Outcome);
        var failureResult = Assert.IsType<LegacyUpgradeResult>(
            result.Parameters[UpgradingDialogViewModel.FailureResultParameter]);
        Assert.Same(failure, failureResult.Failure);

        await viewModel.OnDialogClosedAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task UnexpectedMigrationFailureIsLoggedAndReturnedInsteadOfHanging()
    {
        var coordinator = new FaultingLegacyUpgradeCoordinator();
        var viewModel = new UpgradingDialogViewModel(
            coordinator,
            NullLogger<UpgradingDialogViewModel>.Instance);
        await using var viewModelScope = viewModel.ConfigureAwait(true);

        viewModel.OnDialogOpened(new AppDialogRequest(AppDialog.LegacyUpgrade));
        await coordinator.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var closed = new TaskCompletionSource<AppDialogResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.CloseRequested += (_, result) => closed.TrySetResult(result);
        coordinator.Fail.TrySetResult();

        var result = await closed.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        await viewModel.OnDialogClosedAsync().ConfigureAwait(true);

        Assert.Equal(AppDialogOutcome.Rejected, result.Outcome);
        Assert.False(viewModel.IsMigrationActive);
        var failureResult = Assert.IsType<LegacyUpgradeResult>(
            result.Parameters[UpgradingDialogViewModel.FailureResultParameter]);
        Assert.IsType<NullReferenceException>(failureResult.Failure);
    }

    private sealed class BlockingLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        private IProgress<LegacyUpgradeProgress>? _progress;

        public bool IsUpgradeRequired() => true;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowTermination { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            _progress = progress;
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

        public void ReportProgress(LegacyUpgradeProgress progress)
        {
            (_progress ?? throw new InvalidOperationException("Migration has not started."))
                .Report(progress);
        }
    }

    private static UpgradingDialogViewModel CreateViewModel(
        ILegacyUpgradeCoordinator coordinator)
    {
        return new UpgradingDialogViewModel(
            coordinator,
            NullLogger<UpgradingDialogViewModel>.Instance);
    }

    private static async Task StopMigrationAsync(
        UpgradingDialogViewModel viewModel,
        BlockingLegacyUpgradeCoordinator coordinator)
    {
        var closeTask = viewModel.OnDialogClosedAsync();
        await coordinator.CancellationObserved.Task
            .WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        coordinator.AllowTermination.TrySetResult();
        await closeTask.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private sealed class FaultingLegacyUpgradeCoordinator : ILegacyUpgradeCoordinator
    {
        public bool IsUpgradeRequired() => true;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Fail.Task.ConfigureAwait(false);
            object missingLegacyRecord = null!;
            _ = missingLegacyRecord.ToString();
            throw new InvalidOperationException("The missing legacy record unexpectedly existed.");
        }
    }

    private sealed class CompletedLegacyUpgradeCoordinator(LegacyUpgradeOutcome outcome) : ILegacyUpgradeCoordinator
    {
        public bool IsUpgradeRequired() => true;

        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LegacyUpgradeResult(outcome));
        }
    }

    private sealed class FailedLegacyUpgradeCoordinator(Exception failure) : ILegacyUpgradeCoordinator
    {
        public bool IsUpgradeRequired() => true;

        public Task<LegacyUpgradeResult> UpgradeAsync(
            IProgress<LegacyUpgradeProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LegacyUpgradeResult(
                LegacyUpgradeOutcome.Failed,
                "legacy backup preserved",
                failure));
        }
    }
}
