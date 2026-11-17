using DownKyi.Application.Settings;
namespace DownKyi.Infrastructure.Settings;

public partial class SettingsManager
{
    public UiLanguage UiLanguage => _appSettings.Basic.UiLanguage;

    public bool SetUiLanguage(UiLanguage language)
    {
        return SetProperty(
            _appSettings.Basic.UiLanguage,
            language,
            value => _appSettings.Basic.UiLanguage = value);
    }

    // 默认下载完成后的操作
    private const AfterDownloadOperation AfterDownload = AfterDownloadOperation.None;

    // 是否监听剪贴板
    private const AllowStatus IsListenClipboard = AllowStatus.Yes;

    // 下载完成列表排序
    private const DownloadFinishedSort FinishedSort = DownloadFinishedSort.DownloadAsc;

    public ThemeMode GetThemeMode()
    {
        if (_appSettings.Basic.ThemeMode == ThemeMode.Default)
        {
            // 第一次获取，先设置默认值
            SetThemeMode(ThemeMode.Default);
            return ThemeMode.Default;
        }

        return _appSettings.Basic.ThemeMode;
    }

    public bool SetThemeMode(ThemeMode themeMode)
    {
        return SetProperty(
            _appSettings.Basic.ThemeMode,
            themeMode,
            v => _appSettings.Basic.ThemeMode = v);
    }

    /// <summary>
    /// 获取下载完成后的操作
    /// </summary>
    /// <returns></returns>
    public AfterDownloadOperation GetAfterDownloadOperation()
    {
        if (_appSettings.Basic.AfterDownload == AfterDownloadOperation.NotSet)
        {
            // 第一次获取，先设置默认值
            SetAfterDownloadOperation(AfterDownload);
            return AfterDownload;
        }

        return _appSettings.Basic.AfterDownload;
    }

    /// <summary>
    /// 设置下载完成后的操作
    /// </summary>
    /// <param name="afterDownload"></param>
    /// <returns></returns>
    public bool SetAfterDownloadOperation(AfterDownloadOperation afterDownload)
    {
        return SetProperty(
            _appSettings.Basic.AfterDownload,
            afterDownload,
            v => _appSettings.Basic.AfterDownload = v);
    }

    /// <summary>
    /// 是否监听剪贴板
    /// </summary>
    /// <returns></returns>
    public AllowStatus GetIsListenClipboard()
    {
        if (_appSettings.Basic.IsListenClipboard == AllowStatus.None)
        {
            // 第一次获取，先设置默认值
            SetIsListenClipboard(IsListenClipboard);
            return IsListenClipboard;
        }

        return _appSettings.Basic.IsListenClipboard;
    }

    /// <summary>
    /// 是否监听剪贴板
    /// </summary>
    /// <param name="isListen"></param>
    /// <returns></returns>
    public bool SetIsListenClipboard(AllowStatus isListen)
    {
        return SetProperty(
            _appSettings.Basic.IsListenClipboard,
            isListen,
            v => _appSettings.Basic.IsListenClipboard = v);
    }

    /// <summary>
    /// 获取下载完成列表排序
    /// </summary>
    /// <returns></returns>
    public DownloadFinishedSort GetDownloadFinishedSort()
    {
        if (_appSettings.Basic.DownloadFinishedSort == DownloadFinishedSort.NotSet)
        {
            // 第一次获取，先设置默认值
            SetDownloadFinishedSort(FinishedSort);
            return FinishedSort;
        }

        return _appSettings.Basic.DownloadFinishedSort;
    }

    /// <summary>
    /// 设置下载完成列表排序
    /// </summary>
    /// <param name="finishedSort"></param>
    /// <returns></returns>
    public bool SetDownloadFinishedSort(DownloadFinishedSort finishedSort)
    {
        return SetProperty(
            _appSettings.Basic.DownloadFinishedSort,
            finishedSort,
            v => _appSettings.Basic.DownloadFinishedSort = v);
    }
}
