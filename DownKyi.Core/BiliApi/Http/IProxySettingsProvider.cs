using DownKyi.Core.Settings;

namespace DownKyi.Core.BiliApi.Http;

public interface IProxySettingsProvider
{
    NetworkProxy GetNetworkProxy();

    string GetCustomProxy();
}
