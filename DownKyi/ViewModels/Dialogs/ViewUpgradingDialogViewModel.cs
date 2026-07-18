using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Lifetime;
using DownKyi.Commands;
using DownKyi.Core.Logging;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using Microsoft.Extensions.Logging;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class ViewUpgradingDialogViewModel : BaseDialogViewModel, IDisposable
{
    public const string Tag = "DialogLoading";
    private readonly DownloadListState _downloadLists;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<ViewUpgradingDialogViewModel> _logger;
    private readonly ILegacyUpgradeCoordinator _upgradeCoordinator;
    private CancellationTokenSource? _upgradeCancellation;

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

    private DownKyiAsyncDelegateCommand? _restartCommand;

    public DownKyiAsyncDelegateCommand RestartCommand =>
        _restartCommand ??= new DownKyiAsyncDelegateCommand(ExecuteRestartAsync, _logger);

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
        Message = "数据迁移中，请不要关闭软件";
    }

    public override void OnDialogOpened(AppDialogRequest request)
    {
        CancelUpgrade();
        _upgradeCancellation = new CancellationTokenSource();
        _ = UpgradeAsync(_upgradeCancellation.Token);
    }

    public override void OnDialogClosed()
    {
        CancelUpgrade();
        base.OnDialogClosed();
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
                    CloseDialog(AppDialogOutcome.Canceled);
                    break;
                case LegacyUpgradeOutcome.Completed:
                    _downloadLists.ReplaceDownloaded(result.DownloadedItems);
                    Percent = 100;
                    Message = "下载信息迁移完成";
                    RestartVisible = true;
                    break;
                case LegacyUpgradeOutcome.Failed:
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

    private void CancelUpgrade()
    {
        _upgradeCancellation?.Cancel();
        _upgradeCancellation?.Dispose();
        _upgradeCancellation = null;
    }

    public void Dispose()
    {
        CancelUpgrade();
        GC.SuppressFinalize(this);
    }
}
