using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Lifetime;
using DownKyi.Commands;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class ViewUpgradingDialogViewModel : BaseDialogViewModel, IAsyncDisposable
{
    public const string Tag = "DialogLoading";
    private readonly DownloadListState _downloadLists;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<ViewUpgradingDialogViewModel> _logger;
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

    private string? _message;

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    private bool _restartedVisible;

    public bool RestartVisible
    {
        get => _restartedVisible;
        set => SetProperty(ref _restartedVisible, value);
    }

    public bool IsMigrationActive
    {
        get => _isMigrationActive;
        private set => SetProperty(ref _isMigrationActive, value);
    }

    public bool CancelConfirmationVisible
    {
        get => _cancelConfirmationVisible;
        private set => SetProperty(ref _cancelConfirmationVisible, value);
    }

    private DownKyiAsyncDelegateCommand? _restartCommand;

    public DownKyiAsyncDelegateCommand RestartCommand =>
        _restartCommand ??= new DownKyiAsyncDelegateCommand(ExecuteRestartAsync, _logger);

    private RelayCommand? _requestCancelMigrationCommand;

    public RelayCommand RequestCancelMigrationCommand =>
        _requestCancelMigrationCommand ??= new RelayCommand(ShowCancelConfirmation);

    private RelayCommand? _continueMigrationCommand;

    public RelayCommand ContinueMigrationCommand =>
        _continueMigrationCommand ??= new RelayCommand(HideCancelConfirmation);

    private DownKyiAsyncDelegateCommand? _confirmCancelMigrationCommand;

    public DownKyiAsyncDelegateCommand ConfirmCancelMigrationCommand =>
        _confirmCancelMigrationCommand ??= new DownKyiAsyncDelegateCommand(
            ConfirmCancelMigrationAsync,
            _logger,
            () => IsMigrationActive);

    public ViewUpgradingDialogViewModel(
        ILegacyUpgradeCoordinator upgradeCoordinator,
        DownloadListState downloadLists,
        IApplicationLifecycle applicationLifecycle,
        ILogger<ViewUpgradingDialogViewModel> logger)
    {
        _upgradeCoordinator = upgradeCoordinator ?? throw new ArgumentNullException(nameof(upgradeCoordinator));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        _applicationLifecycle = applicationLifecycle
            ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Message = "数据迁移中；如需停止，请使用“取消迁移”";
    }

    public override void OnDialogOpened(AppDialogRequest request)
    {
        if (_upgradeCancellation is not null)
        {
            throw new InvalidOperationException("Legacy data migration is already running.");
        }

        _stopTask = null;
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
                    _downloadLists.ReplaceDownloaded(result.DownloadedItems);
                    Percent = 100;
                    Message = "下载信息迁移完成";
                    RestartVisible = true;
                    break;
                case LegacyUpgradeOutcome.Failed:
                    FinishMigrationActivity();
                    Message = result.ErrorMessage ?? "数据迁移失败，请查看日志";
                    RestartVisible = false;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported legacy upgrade outcome: {result.Outcome}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogErrorMessage("Legacy data migration dialog failed.", e);
            FinishMigrationActivity();
            Message = "数据迁移失败，请查看日志";
            RestartVisible = false;
        }
    }

    private void ApplyProgress(LegacyUpgradeProgress progress)
    {
        Message = progress.Message;
        if (progress.Percent is { } percent)
        {
            Percent = percent;
        }
    }

    private async Task ExecuteRestartAsync()
    {
        if (!await _applicationLifecycle.RestartAsync().ConfigureAwait(true))
        {
            Message = "无法重新启动应用，请查看日志";
            RestartVisible = true;
        }
    }

    private void ShowCancelConfirmation()
    {
        if (IsMigrationActive)
        {
            CancelConfirmationVisible = true;
        }
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
