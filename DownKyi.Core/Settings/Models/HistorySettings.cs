namespace DownKyi.Core.Settings.Models;

public sealed class HistorySettings
{
    public bool IsAutoRefreshEnabled { get; set; }
    public decimal AutoRefreshIntervalSeconds { get; set; } =
        ApplicationSettingsDefaults.DefaultHistoryAutoRefreshIntervalSeconds;
}
