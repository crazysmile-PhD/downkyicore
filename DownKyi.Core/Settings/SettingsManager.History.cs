namespace DownKyi.Core.Settings;

public partial class SettingsManager
{
    public bool IsHistoryAutoRefreshEnabled()
    {
        return _appSettings.History.IsAutoRefreshEnabled;
    }

    public bool SetHistoryAutoRefreshEnabled(bool isEnabled)
    {
        return SetProperty(
            _appSettings.History.IsAutoRefreshEnabled,
            isEnabled,
            value => _appSettings.History.IsAutoRefreshEnabled = value);
    }

    public decimal GetHistoryAutoRefreshIntervalSeconds()
    {
        var interval = NormalizeHistoryAutoRefreshInterval(
            _appSettings.History.AutoRefreshIntervalSeconds);
        if (interval != _appSettings.History.AutoRefreshIntervalSeconds)
        {
            SetHistoryAutoRefreshIntervalSeconds(interval);
        }

        return interval;
    }

    public bool SetHistoryAutoRefreshIntervalSeconds(decimal seconds)
    {
        var interval = NormalizeHistoryAutoRefreshInterval(seconds);
        return SetProperty(
            _appSettings.History.AutoRefreshIntervalSeconds,
            interval,
            value => _appSettings.History.AutoRefreshIntervalSeconds = value);
    }

    private static decimal NormalizeHistoryAutoRefreshInterval(decimal seconds)
    {
        if (seconds <= 0)
        {
            return ApplicationSettingsDefaults.DefaultHistoryAutoRefreshIntervalSeconds;
        }

        return Math.Max(
            ApplicationSettingsDefaults.MinimumHistoryAutoRefreshIntervalSeconds,
            Math.Round(seconds, 2, MidpointRounding.AwayFromZero));
    }
}
