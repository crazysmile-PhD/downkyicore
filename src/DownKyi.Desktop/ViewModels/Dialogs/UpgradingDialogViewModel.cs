using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Commands;
using DownKyi.Services.Migration;
using DownKyi.Utils;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class UpgradingDialogViewModel : BaseDialogViewModel, IAsyncDisposable
{
    public const string Tag = "DialogLoading";
    internal const string FailureResultParameter = "failureResult";
    private readonly ILogger<UpgradingDialogViewModel> _logger;
    private readonly ILegacyUpgradeCoordinator _upgradeCoordinator;
    private CancellationTokenSource? _upgradeCancellation;
    private Task? _upgradeTask;
    private Task? _stopTask;
    private bool _isMigrationActive;
    private bool _cancelConfirmationVisible;

    private double _percent;

    public double Percent
    {
        get => _percent;
        set => SetProperty(ref _percent, value);
    }

    private bool _isProgressVisible;

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set => SetProperty(ref _isProgressVisible, value);
    }

    private string? _message;

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool IsMigrationActive
    {
        get => _isMigrationActive;
        private set
        {
            if (SetProperty(ref _isMigrationActive, value))
            {
                _confirmCancelMigrationCommand?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CancelConfirmationVisible
    {
        get => _cancelConfirmationVisible;
        private set => SetProperty(ref _cancelConfirmationVisible, value);
    }

    private RelayCommand? _requestCancelMigrationCommand;

    public RelayCommand RequestCancelMigrationCommand =>
        _requestCancelMigrationCommand ??= new RelayCommand(RequestCancelOrClose);

    private RelayCommand? _continueMigrationCommand;

    public RelayCommand ContinueMigrationCommand =>
        _continueMigrationCommand ??= new RelayCommand(HideCancelConfirmation);

    private DownKyiAsyncDelegateCommand? _confirmCancelMigrationCommand;

    public DownKyiAsyncDelegateCommand ConfirmCancelMigrationCommand =>
        _confirmCancelMigrationCommand ??= new DownKyiAsyncDelegateCommand(
            ConfirmCancelMigrationAsync,
            _logger,
            () => IsMigrationActive);

    public UpgradingDialogViewModel(
        ILegacyUpgradeCoordinator upgradeCoordinator,
        ILogger<UpgradingDialogViewModel> logger)
    {
        _upgradeCoordinator = upgradeCoordinator ?? throw new ArgumentNullException(nameof(upgradeCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Message = DictionaryResource.GetIfApplicationInitialized<string>("MigrationInProgress")
            ?? "数据迁移中；如需停止，请使用“取消迁移”";
    }

    public override void OnDialogOpened(AppDialogRequest request)
    {
        if (_upgradeCancellation is not null)
        {
            throw new InvalidOperationException("Legacy data migration is already running.");
        }

        _stopTask = null;
        Percent = 0;
        IsProgressVisible = false;
        IsMigrationActive = true;
        CancelConfirmationVisible = false;
        _upgradeCancellation = new CancellationTokenSource();
        _upgradeTask = UpgradeAsync(_upgradeCancellation.Token);
    }

    public override bool CanCloseDialog()
    {
        if (!IsMigrationActive)
        {
            return true;
        }

        ShowCancelConfirmation();
        return false;
    }

    public override async Task OnDialogClosedAsync()
    {
        await StopUpgradeAsync().ConfigureAwait(true);
        FinishMigrationActivity();
        await base.OnDialogClosedAsync().ConfigureAwait(true);
    }

    private async Task UpgradeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var progress = new Progress<LegacyUpgradeProgress>(ApplyProgress);
            var result = await _upgradeCoordinator
                .UpgradeAsync(progress, cancellationToken)
                .ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            switch (result.Outcome)
            {
                case LegacyUpgradeOutcome.NoMigration:
                    FinishMigrationActivity();
                    CloseDialog(AppDialogOutcome.Canceled);
                    break;
                case LegacyUpgradeOutcome.Completed:
                    FinishMigrationActivity();
                    Percent = 100;
                    IsProgressVisible = true;
                    Message = DictionaryResource.GetString("MigrationDownloadComplete", "下载信息迁移完成");
                    CloseDialog(AppDialogOutcome.Accepted);
                    break;
                case LegacyUpgradeOutcome.Failed:
                    FinishMigrationActivity();
                    Message = result.ErrorMessage ?? DictionaryResource.GetString("MigrationFailedSeeLogs", "数据迁移失败，请查看日志");
                    var failure = result.Failure ?? new InvalidOperationException(Message);
                    CloseDialog(
                        AppDialogOutcome.Rejected,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            [FailureResultParameter] = result with { Failure = failure }
                        });
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported legacy upgrade outcome: {result.Outcome}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e) when (IsRecoverableUiFailure(e))
        {
            _logger.LogErrorMessage("Legacy data migration dialog failed.", e);
            FinishMigrationActivity();
            Message = DictionaryResource.GetString("MigrationFailedSeeLogs", "数据迁移失败，请查看日志");
            CloseDialog(
                AppDialogOutcome.Rejected,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [FailureResultParameter] = new LegacyUpgradeResult(
                        LegacyUpgradeOutcome.Failed,
                        Message,
                        e)
                });
        }
    }

    private void ApplyProgress(LegacyUpgradeProgress progress)
    {
        Message = progress.Message;
        if (progress.Percent is { } percent)
        {
            Percent = percent;
            IsProgressVisible = true;
        }
    }

    private static bool IsRecoverableUiFailure(Exception exception)
    {
        return exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
    }

    private void ShowCancelConfirmation()
    {
        if (IsMigrationActive)
        {
            CancelConfirmationVisible = true;
        }
    }

    private void RequestCancelOrClose()
    {
        if (IsMigrationActive)
        {
            ShowCancelConfirmation();
            return;
        }

        CloseDialog(AppDialogOutcome.Canceled);
    }

    private void HideCancelConfirmation()
    {
        CancelConfirmationVisible = false;
    }

    private async Task ConfirmCancelMigrationAsync()
    {
        if (!IsMigrationActive)
        {
            return;
        }

        await StopUpgradeAsync().ConfigureAwait(true);
        FinishMigrationActivity();
        CloseDialog(AppDialogOutcome.Canceled);
    }

    private void FinishMigrationActivity()
    {
        IsMigrationActive = false;
        CancelConfirmationVisible = false;
    }

    private Task StopUpgradeAsync()
    {
        return _stopTask ??= StopUpgradeCoreAsync();
    }

    private async Task StopUpgradeCoreAsync()
    {
        var cancellation = _upgradeCancellation;
        var upgradeTask = _upgradeTask;
        if (cancellation is null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(
                    cancellation.CancelAsync(),
                    upgradeTask ?? Task.CompletedTask)
                .ConfigureAwait(true);
        }
        finally
        {
            cancellation.Dispose();
            if (ReferenceEquals(_upgradeCancellation, cancellation))
            {
                _upgradeCancellation = null;
                _upgradeTask = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_upgradeCancellation is not null)
        {
            await StopUpgradeAsync().ConfigureAwait(true);
        }

        GC.SuppressFinalize(this);
    }
}
