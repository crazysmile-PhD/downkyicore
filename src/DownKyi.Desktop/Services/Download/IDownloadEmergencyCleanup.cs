using DownKyi.Core.Aria2cNet.Server;

namespace DownKyi.Services.Download;

internal interface IDownloadEmergencyCleanup
{
    void KillTrackedRuntime(string reason);
}

internal sealed class AriaDownloadEmergencyCleanup(AriaServer ariaServer) : IDownloadEmergencyCleanup
{
    private readonly AriaServer _ariaServer = ariaServer
        ?? throw new ArgumentNullException(nameof(ariaServer));

    public void KillTrackedRuntime(string reason)
    {
        _ariaServer.KillTrackedServer(reason);
    }
}
