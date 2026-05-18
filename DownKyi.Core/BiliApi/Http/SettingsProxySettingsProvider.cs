using DownKyi.Core.Settings;

namespace DownKyi.Core.BiliApi.Http;

public class SettingsProxySettingsProvider : IProxySettingsProvider
{
    public NetworkProxy GetNetworkProxy()
    {
        return SettingsManager.GetInstance().GetNetworkProxy();
    }

    public string GetCustomProxy()
    {
        return SettingsManager.GetInstance().GetCustomProxy();
    }
}
