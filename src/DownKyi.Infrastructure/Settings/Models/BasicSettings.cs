using DownKyi.Application.Settings;
namespace DownKyi.Infrastructure.Settings.Models;

/// <summary>
/// 基本
/// </summary>
public class BasicSettings
{
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Default;
    public UiLanguage UiLanguage { get; set; } = UiLanguage.ChineseSimplified;
    public AfterDownloadOperation AfterDownload { get; set; } = AfterDownloadOperation.NotSet;
    public AllowStatus IsListenClipboard { get; set; } = AllowStatus.None;
    public DownloadFinishedSort DownloadFinishedSort { get; set; } = DownloadFinishedSort.NotSet;
}
