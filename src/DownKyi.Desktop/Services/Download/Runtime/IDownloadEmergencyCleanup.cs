namespace DownKyi.Services.Download;

internal interface IDownloadEmergencyCleanup
{
    void KillTrackedRuntime(string reason);
}

internal sealed class BuiltinDownloadEmergencyCleanup : IDownloadEmergencyCleanup
{
    public void KillTrackedRuntime(string reason)
    {
        // Built-in transfers run in-process and are cancelled by the normal runtime shutdown path.
    }
}
