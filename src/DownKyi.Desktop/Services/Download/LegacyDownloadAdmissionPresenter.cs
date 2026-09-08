using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Application.Downloads;
using DownKyi.Utils;

namespace DownKyi.Services.Download;

internal sealed class LegacyDownloadAdmissionPresenter
{
    private readonly IDownloadTaskApplicationService _tasks;
    private readonly IAppDialogService _dialogs;

    public LegacyDownloadAdmissionPresenter(
        IDownloadTaskApplicationService tasks,
        IAppDialogService dialogs)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public async Task<bool> EnsureAdmissionAsync(CancellationToken cancellationToken)
    {
        var admission = await _tasks.CheckNewDownloadAdmissionAsync(cancellationToken)
            .ConfigureAwait(true);
        if (admission.IsSuccess)
        {
            return true;
        }

        if (!DownloadAdmissionErrors.IsLegacyUpgradeBlocked(admission.Error))
        {
            throw new InvalidOperationException(
                admission.Error?.Message ?? "Download admission failed without an error.");
        }

        var alert = new AlertService(_dialogs);
        var outcome = await alert.ShowWarning(
            DictionaryResource.GetString("LegacyDownloadAdmissionBlocked"),
            buttonNumber: 2,
            cancellationToken).ConfigureAwait(true);
        if (outcome != AppDialogOutcome.Accepted)
        {
            return false;
        }

        var confirmation = await _tasks
            .ConfirmLegacyRemoteTasksStoppedAsync(cancellationToken)
            .ConfigureAwait(true);
        if (confirmation.IsSuccess)
        {
            return true;
        }

        await alert.ShowError(
            confirmation.Error?.Message
                ?? DictionaryResource.GetString("LegacyDownloadAdmissionConfirmationFailed"),
            cancellationToken).ConfigureAwait(true);
        return false;
    }
}
