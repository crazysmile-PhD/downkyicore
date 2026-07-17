namespace DownKyi.Core.Settings;

public partial class SettingsManager
{
    public const int HighSpeedBuiltInSplit = ApplicationSettingsDefaults.HighSpeedBuiltInSplit;
    public const int HighSpeedAriaSplit = ApplicationSettingsDefaults.HighSpeedAriaSplit;
    public const int HighSpeedAriaMaxConnectionPerServer = ApplicationSettingsDefaults.HighSpeedAriaMaxConnectionPerServer;
    public const int HighSpeedAriaMinSplitSize = ApplicationSettingsDefaults.HighSpeedAriaMinSplitSize;

    private const AllowStatus HighSpeedDownloadMode = AllowStatus.No;
    private const int AriaMaxConnectionPerServer = 8;
    private const int AriaMinSplitSize = 10;

    public AllowStatus GetHighSpeedDownloadMode()
    {
        if (_appSettings.Network.HighSpeedDownloadMode != AllowStatus.None)
        {
            return _appSettings.Network.HighSpeedDownloadMode;
        }

        SetHighSpeedDownloadMode(HighSpeedDownloadMode);
        return HighSpeedDownloadMode;
    }

    public bool SetHighSpeedDownloadMode(AllowStatus highSpeedDownloadMode)
    {
        return SetProperty(
            _appSettings.Network.HighSpeedDownloadMode,
            highSpeedDownloadMode,
            v => _appSettings.Network.HighSpeedDownloadMode = v);
    }

    public int GetAriaMaxConnectionPerServer()
    {
        if (_appSettings.Network.AriaMaxConnectionPerServer != -1)
        {
            return _appSettings.Network.AriaMaxConnectionPerServer;
        }

        SetAriaMaxConnectionPerServer(AriaMaxConnectionPerServer);
        return AriaMaxConnectionPerServer;
    }

    public bool SetAriaMaxConnectionPerServer(int ariaMaxConnectionPerServer)
    {
        var value = Math.Clamp(ariaMaxConnectionPerServer, 1, 16);
        return SetProperty(
            _appSettings.Network.AriaMaxConnectionPerServer,
            value,
            v => _appSettings.Network.AriaMaxConnectionPerServer = v);
    }

    public int GetAriaMinSplitSize()
    {
        if (_appSettings.Network.AriaMinSplitSize != -1)
        {
            return _appSettings.Network.AriaMinSplitSize;
        }

        SetAriaMinSplitSize(AriaMinSplitSize);
        return AriaMinSplitSize;
    }

    public bool SetAriaMinSplitSize(int ariaMinSplitSize)
    {
        var value = Math.Clamp(ariaMinSplitSize, 1, 1024);
        return SetProperty(
            _appSettings.Network.AriaMinSplitSize,
            value,
            v => _appSettings.Network.AriaMinSplitSize = v);
    }
}
