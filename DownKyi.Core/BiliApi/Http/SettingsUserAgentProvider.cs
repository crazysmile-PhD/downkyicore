using DownKyi.Core.Settings;

namespace DownKyi.Core.BiliApi.Http;

public class SettingsUserAgentProvider : IUserAgentProvider
{
    public string GetUserAgent()
    {
        return SettingsManager.GetInstance().GetUserAgent();
    }
}
