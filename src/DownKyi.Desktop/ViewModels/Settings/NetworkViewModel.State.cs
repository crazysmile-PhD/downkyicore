using System;
using System.Collections.Generic;
using DownKyi.Application.Settings;

namespace DownKyi.ViewModels.Settings;

internal partial class NetworkViewModel
{
    #region 页面属性申明

    private string _userAgent = string.Empty;

    public string UserAgent
    {
        get => _userAgent;
        set => SetProperty(ref _userAgent, value);
    }

    private bool _highSpeedDownloadMode;

    public bool HighSpeedDownloadMode
    {
        get => _highSpeedDownloadMode;
        set => SetProperty(ref _highSpeedDownloadMode, value);
    }

    private IReadOnlyList<int> _maxCurrentDownloads = Array.Empty<int>();

    public IReadOnlyList<int> MaxCurrentDownloads
    {
        get => _maxCurrentDownloads;
        set => SetProperty(ref _maxCurrentDownloads, value);
    }

    private int _selectedMaxCurrentDownload;

    public int SelectedMaxCurrentDownload
    {
        get => _selectedMaxCurrentDownload;
        set => SetProperty(ref _selectedMaxCurrentDownload, value);
    }

    private NetworkProxy _networkProxy;

    public NetworkProxy NetworkProxy
    {
        get => _networkProxy;
        set => SetProperty(ref _networkProxy, value);
    }

    private string? _customNetworkProxy;

    public string? CustomNetworkProxy
    {
        get => _customNetworkProxy;
        set => SetProperty(ref _customNetworkProxy, value);
    }

    private IReadOnlyList<int> _splits = Array.Empty<int>();

    public IReadOnlyList<int> Splits
    {
        get => _splits;
        set => SetProperty(ref _splits, value);
    }

    private int _selectedSplit;

    public int SelectedSplit
    {
        get => _selectedSplit;
        set => SetProperty(ref _selectedSplit, value);
    }

    private int _maxOverallDownloadLimit;

    public int MaxOverallDownloadLimit
    {
        get => _maxOverallDownloadLimit;
        set => SetProperty(ref _maxOverallDownloadLimit, value);
    }

    private int _maxDownloadLimit;

    public int MaxDownloadLimit
    {
        get => _maxDownloadLimit;
        set => SetProperty(ref _maxDownloadLimit, value);
    }

    private bool _isHttpProxy;

    public bool IsHttpProxy
    {
        get => _isHttpProxy;
        set => SetProperty(ref _isHttpProxy, value);
    }

    private string _httpProxy = string.Empty;

    public string HttpProxy
    {
        get => _httpProxy;
        set => SetProperty(ref _httpProxy, value);
    }

    private int _httpProxyPort;

    public int HttpProxyPort
    {
        get => _httpProxyPort;
        set => SetProperty(ref _httpProxyPort, value);
    }

    #endregion
}
