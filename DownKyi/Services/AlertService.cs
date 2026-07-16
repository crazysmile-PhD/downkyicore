using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Desktop;
using DownKyi.Images;
using DownKyi.Utils;

namespace DownKyi.Services;

internal class AlertService
{
    private readonly IAppDialogService _dialogService;

    public AlertService(IAppDialogService dialogService)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <summary>
    /// 显示一个信息弹窗
    /// </summary>
    /// <param name="message"></param>
    /// <param name="buttonNumber"></param>
    /// <returns></returns>
    public Task<AppDialogOutcome> ShowInfo(
        string message,
        int buttonNumber = 2,
        CancellationToken cancellationToken = default)
    {
        var image = SystemIcon.Instance().Info;
        var title = DictionaryResource.GetString("Info");
        return ShowMessage(image, title, message, buttonNumber, cancellationToken);
    }

    /// <summary>
    /// 显示一个警告弹窗
    /// </summary>
    /// <param name="message"></param>
    /// <param name="buttonNumber"></param>
    /// <returns></returns>
    public Task<AppDialogOutcome> ShowWarning(
        string message,
        int buttonNumber = 1,
        CancellationToken cancellationToken = default)
    {
        var image = SystemIcon.Instance().Warning;
        var title = DictionaryResource.GetString("Warning");
        return ShowMessage(image, title, message, buttonNumber, cancellationToken);
    }

    /// <summary>
    /// 显示一个错误弹窗
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public Task<AppDialogOutcome> ShowError(
        string message,
        CancellationToken cancellationToken = default)
    {
        var image = SystemIcon.Instance().Error;
        var title = DictionaryResource.GetString("Error");
        return ShowMessage(image, title, message, 1, cancellationToken);
    }

    public async Task<AppDialogOutcome> ShowMessage(
        VectorImage image,
        string title,
        string message,
        int buttonNumber,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["image"] = image,
            ["title"] = title,
            ["message"] = message,
            ["button_number"] = buttonNumber
        };
        var result = await _dialogService.ShowAsync(
            new AppDialogRequest(AppDialog.Alert, parameters),
            cancellationToken).ConfigureAwait(true);
        return result.Outcome;
    }
}
