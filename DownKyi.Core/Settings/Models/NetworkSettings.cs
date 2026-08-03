using DownKyi.Core.Aria2cNet.Server;
using Newtonsoft.Json;

namespace DownKyi.Core.Settings.Models;

/// <summary>
/// 网络
/// </summary>
public class NetworkSettings
{
    public AllowStatus IsLiftingOfRegion { get; set; } = AllowStatus.None;

    [JsonProperty("UseSsl")]
    private AllowStatus LegacyUseSsl
    {
        set => HasLegacyUseSsl = true;
    }

    [JsonIgnore]
    internal bool HasLegacyUseSsl { get; private set; }

    internal bool ConsumeLegacyHttpsSwitch()
    {
        var wasPresent = HasLegacyUseSsl;
        HasLegacyUseSsl = false;
        return wasPresent;
    }

    public string UserAgent { get; set; } = string.Empty;

    public Downloader Downloader { get; set; } = Downloader.NotSet;
    public AllowStatus HighSpeedDownloadMode { get; set; } = AllowStatus.None;

    public NetworkProxy NetworkProxy { get; set; } = NetworkProxy.None;

    public string CustomNetworkProxy { get; set; } = string.Empty;

    #region built-in

    public int MaxCurrentDownloads { get; set; } = -1;
    public int Split { get; set; } = -1;
    public AllowStatus IsHttpProxy { get; set; } = AllowStatus.None;
    public string? HttpProxy { get; set; }
    public int HttpProxyListenPort { get; set; } = -1;

    #endregion

    #region Aria

    public string? AriaToken { get; set; }
    public string? AriaHost { get; set; }

    public int AriaListenPort { get; set; } = -1;

    public AriaConfigLogLevel AriaLogLevel { get; set; } = AriaConfigLogLevel.NotSet;
    public int AriaSplit { get; set; } = -1;
    public int AriaMaxConnectionPerServer { get; set; } = -1;
    public int AriaMinSplitSize { get; set; } = -1;
    public int AriaMaxOverallDownloadLimit { get; set; } = -1;
    public int AriaMaxDownloadLimit { get; set; } = -1;
    public AriaConfigFileAllocation AriaFileAllocation { get; set; } = AriaConfigFileAllocation.NotSet;

    public AllowStatus IsAriaHttpProxy { get; set; } = AllowStatus.None;
    public string? AriaHttpProxy { get; set; }
    public int AriaHttpProxyListenPort { get; set; } = -1;

    #endregion
}
