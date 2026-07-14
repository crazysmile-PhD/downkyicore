using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Core.Logging;
using DownKyi.Services.Download;
using DownKyi.Services.Migration;
using Prism.Commands;
using Prism.Dialogs;

namespace DownKyi.ViewModels.Dialogs;

internal sealed class ViewUpgradingDialogViewModel : BaseDialogViewModel, IDisposable
{
    public const string Tag = "DialogLoading";
    private readonly DownloadListState _downloadLists;
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

    private DelegateCommand? _restartCommand;

    public DelegateCommand RestartCommand => _restartCommand ??= new DelegateCommand(ExecuteRestart);

    public ViewUpgradingDialogViewModel(
        ILegacyUpgradeCoordinator upgradeCoordinator,
        DownloadListState downloadLists)
    {
        _upgradeCoordinator = upgradeCoordinator ?? throw new ArgumentNullException(nameof(upgradeCoordinator));
        _downloadLists = downloadLists ?? throw new ArgumentNullException(nameof(downloadLists));
        Message = "数据迁移中，请不要关闭软件";
    }

    public override void OnDialogOpened(IDialogParameters parameters)
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
                    CloseDialog(new DialogResult());
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
        }
        catch (InvalidOperationException e)
        {
            LogManager.Error(nameof(ViewUpgradingDialogViewModel), e);
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

    private void ExecuteRestart()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (executablePath == null)
        {
            return;
        }

        Process.Start(executablePath);
        App.Current.AppLife?.Shutdown();
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
