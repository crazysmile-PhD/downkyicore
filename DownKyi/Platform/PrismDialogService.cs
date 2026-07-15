using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.ViewModels.Dialogs;
using Prism.Dialogs;
using LegacyDialogService = DownKyi.PrismExtension.Dialog.IDialogService;

namespace DownKyi.Platform;

internal sealed class PrismDialogService : IAppDialogService
{
    private readonly LegacyDialogService _dialogService;

    public PrismDialogService(LegacyDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public async Task<AppDialogResult> ShowAsync(
        AppDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = new DialogParameters();
        if (request.Parameters != null)
        {
            foreach (var pair in request.Parameters)
            {
                parameters.Add(pair.Key, pair.Value ?? string.Empty);
            }
        }

        var response = new AppDialogResult(AppDialogOutcome.None, new Dictionary<string, object?>());
        await _dialogService.ShowDialogAsync(
            GetDialogName(request.Dialog),
            parameters,
            result => response = ConvertResult(result)).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    internal static string GetDialogName(AppDialog dialog)
    {
        return dialog switch
        {
            AppDialog.Alert => ViewAlertDialogViewModel.Tag,
            AppDialog.DownloadSettings => ViewDownloadSetterViewModel.Tag,
            AppDialog.ParsingSelector => ViewParsingSelectorViewModel.Tag,
            AppDialog.AlreadyDownloaded => ViewAlreadyDownloadedDialogViewModel.Tag,
            AppDialog.NewVersionAvailable => NewVersionAvailableDialogViewModel.Tag,
            AppDialog.LegacyUpgrade => ViewUpgradingDialogViewModel.Tag,
            _ => throw new ArgumentOutOfRangeException(nameof(dialog), dialog, null)
        };
    }

    private static AppDialogResult ConvertResult(IDialogResult result)
    {
        var outcome = result.Result switch
        {
            ButtonResult.OK => AppDialogOutcome.Accepted,
            ButtonResult.No => AppDialogOutcome.Rejected,
            ButtonResult.Cancel => AppDialogOutcome.Canceled,
            _ => AppDialogOutcome.None
        };
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in result.Parameters)
        {
            parameters[pair.Key] = pair.Value;
        }

        return new AppDialogResult(outcome, parameters);
    }
}
