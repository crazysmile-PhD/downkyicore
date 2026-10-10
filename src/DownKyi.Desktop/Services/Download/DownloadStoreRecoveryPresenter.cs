using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Application.Lifetime;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Utils;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadStoreRecoveryPresenter
{
    private readonly IAppDialogService _dialogs;
    private readonly IDownloadStoreRecovery _recovery;
    private readonly IApplicationLifecycle _applicationLifecycle;
    private readonly ILogger<DownloadStoreRecoveryPresenter> _logger;

    public DownloadStoreRecoveryPresenter(
        IAppDialogService dialogs,
        IDownloadStoreRecovery recovery,
        IApplicationLifecycle applicationLifecycle,
        ILogger<DownloadStoreRecoveryPresenter> logger)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _applicationLifecycle = applicationLifecycle
            ?? throw new ArgumentNullException(nameof(applicationLifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static bool CanReset(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is DownloadStoreSchemaMismatchException)
        {
            return true;
        }

        if (failure is AggregateException aggregate
            && aggregate.InnerExceptions.Any(CanReset))
        {
            return true;
        }

        return failure.InnerException != null && CanReset(failure.InnerException);
    }

    public async Task ConfirmResetAndRestartAsync(
        Exception failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!CanReset(failure))
        {
            return;
        }

        var alert = new AlertService(_dialogs);
        var confirmation = await alert.ShowWarning(
            DictionaryResource.GetString("DownloadStoreResetConfirmation"),
            buttonNumber: 2,
            cancellationToken).ConfigureAwait(true);
        if (confirmation != AppDialogOutcome.Accepted)
        {
            return;
        }

        try
        {
            await _recovery.BackupAndResetAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or SqliteException)
        {
            _logger.LogErrorMessage("Download database backup and reset failed.", exception);
            await alert.ShowError(
                DictionaryResource.GetString("DownloadStoreResetFailed"),
                CancellationToken.None).ConfigureAwait(true);
            return;
        }

        if (!await _applicationLifecycle.RestartAsync(CancellationToken.None).ConfigureAwait(true))
        {
            await alert.ShowError(
                DictionaryResource.GetString("DownloadStoreResetRestartFailed"),
                CancellationToken.None).ConfigureAwait(true);
        }
    }
}
