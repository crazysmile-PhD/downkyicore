using System;
using System.Collections.Generic;
using DownKyi.Core.Settings;

namespace DownKyi.ViewModels.Settings;

internal partial class ViewNetworkViewModel
{
    #region 页面属性申明

    private bool _useSsl;

    public bool UseSsl
    {
        get => _useSsl;
        set => SetProperty(ref _useSsl, value);
    }

    private string _userAgent = string.Empty;

    public string UserAgent
    {
        get => _userAgent;
        set => SetProperty(ref _userAgent, value);
    }

    private bool _builtin;

    public bool Builtin
    {
        get => _builtin;
        set => SetProperty(ref _builtin, value);
    }

    private bool _aria2C;

    public bool Aria2C
    {
        get => _aria2C;
        set => SetProperty(ref _aria2C, value);
    }

    private bool _customAria2C;

    public bool CustomAria2C
    {
        get => _customAria2C;
        set => SetProperty(ref _customAria2C, value);
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

    private string _ariaHost = string.Empty;

    public string AriaHost
    {
        get => _ariaHost;
        set => SetProperty(ref _ariaHost, value);
    }

    private int _ariaListenPort;

    public int AriaListenPort
    {
        get => _ariaListenPort;
        set => SetProperty(ref _ariaListenPort, value);
    }

    private string _ariaToken = string.Empty;

    public string AriaToken
    {
        get => _ariaToken;
        set => SetProperty(ref _ariaToken, value);
    }

    private IReadOnlyList<string> _ariaLogLevels = Array.Empty<string>();

    public IReadOnlyList<string> AriaLogLevels
    {
        get => _ariaLogLevels;
        set => SetProperty(ref _ariaLogLevels, value);
    }

    private string _selectedAriaLogLevel = string.Empty;

    public string SelectedAriaLogLevel
    {
        get => _selectedAriaLogLevel;
        set => SetProperty(ref _selectedAriaLogLevel, value);
    }

    private IReadOnlyList<int> _ariaMaxConcurrentDownloads = Array.Empty<int>();

    public IReadOnlyList<int> AriaMaxConcurrentDownloads
    {
        get => _ariaMaxConcurrentDownloads;
        set => SetProperty(ref _ariaMaxConcurrentDownloads, value);
    }

    private int _selectedAriaMaxConcurrentDownload;

    public int SelectedAriaMaxConcurrentDownload
    {
        get => _selectedAriaMaxConcurrentDownload;
        set => SetProperty(ref _selectedAriaMaxConcurrentDownload, value);
    }

    private IReadOnlyList<int> _ariaSplits = Array.Empty<int>();

    public IReadOnlyList<int> AriaSplits
    {
        get => _ariaSplits;
        set => SetProperty(ref _ariaSplits, value);
    }

    private int _selectedAriaSplit;

    public int SelectedAriaSplit
    {
        get => _selectedAriaSplit;
        set => SetProperty(ref _selectedAriaSplit, value);
    }

    private IReadOnlyList<int> _ariaMaxConnectionPerServers = Array.Empty<int>();

    public IReadOnlyList<int> AriaMaxConnectionPerServers
    {
        get => _ariaMaxConnectionPerServers;
        set => SetProperty(ref _ariaMaxConnectionPerServers, value);
    }

    private int _selectedAriaMaxConnectionPerServer;

    public int SelectedAriaMaxConnectionPerServer
    {
        get => _selectedAriaMaxConnectionPerServer;
        set => SetProperty(ref _selectedAriaMaxConnectionPerServer, value);
    }

    private IReadOnlyList<int> _ariaMinSplitSizes = Array.Empty<int>();

    public IReadOnlyList<int> AriaMinSplitSizes
    {
        get => _ariaMinSplitSizes;
        set => SetProperty(ref _ariaMinSplitSizes, value);
    }

    private int _selectedAriaMinSplitSize;

    public int SelectedAriaMinSplitSize
    {
        get => _selectedAriaMinSplitSize;
        set => SetProperty(ref _selectedAriaMinSplitSize, value);
    }

    private int _ariaMaxOverallDownloadLimit;

    public int AriaMaxOverallDownloadLimit
    {
        get => _ariaMaxOverallDownloadLimit;
        set => SetProperty(ref _ariaMaxOverallDownloadLimit, value);
    }

    private int _ariaMaxDownloadLimit;

    public int AriaMaxDownloadLimit
    {
        get => _ariaMaxDownloadLimit;
        set => SetProperty(ref _ariaMaxDownloadLimit, value);
    }

    private bool _isAriaHttpProxy;

    public bool IsAriaHttpProxy
    {
        get => _isAriaHttpProxy;
        set => SetProperty(ref _isAriaHttpProxy, value);
    }

    private string _ariaHttpProxy = string.Empty;

    public string AriaHttpProxy
    {
        get => _ariaHttpProxy;
        set => SetProperty(ref _ariaHttpProxy, value);
    }

    private int _ariaHttpProxyPort;

    public int AriaHttpProxyPort
    {
        get => _ariaHttpProxyPort;
        set => SetProperty(ref _ariaHttpProxyPort, value);
    }

    private IReadOnlyList<string> _ariaFileAllocations = Array.Empty<string>();

    public IReadOnlyList<string> AriaFileAllocations
    {
        get => _ariaFileAllocations;
        set => SetProperty(ref _ariaFileAllocations, value);
    }

    private string _selectedAriaFileAllocation = string.Empty;

    public string SelectedAriaFileAllocation
    {
        get => _selectedAriaFileAllocation;
        set => SetProperty(ref _selectedAriaFileAllocation, value);
    }

    #endregion
}
