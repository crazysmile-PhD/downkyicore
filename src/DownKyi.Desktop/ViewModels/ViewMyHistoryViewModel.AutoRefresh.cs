using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DownKyi.Core.Settings;
using DownKyi.Utils;

namespace DownKyi.ViewModels;

internal partial class ViewMyHistoryViewModel
{
    private readonly ISettingsStore _settingsStore;
    private readonly CompositeFormat _nextRefreshFormat;
    private CancellationTokenSource? _autoRefreshCancellation;
    private bool _isPageActive;

    private bool _isHistoryAutoRefreshEnabled;

    public bool IsHistoryAutoRefreshEnabled
    {
        get => _isHistoryAutoRefreshEnabled;
        set
        {
            if (!SetProperty(ref _isHistoryAutoRefreshEnabled, value))
            {
                return;
            }

            _settingsStore.Update(settings => settings with
            {
                History = settings.History with { IsAutoRefreshEnabled = value }
            });
            RestartAutoRefresh();
        }
    }

    private decimal _historyAutoRefreshIntervalSeconds;

    public decimal HistoryAutoRefreshIntervalSeconds
    {
        get => _historyAutoRefreshIntervalSeconds;
        set
        {
            var normalized = NormalizeAutoRefreshInterval(value);
            if (!SetProperty(ref _historyAutoRefreshIntervalSeconds, normalized))
            {
                return;
            }

            _settingsStore.Update(settings => settings with
            {
                History = settings.History with { AutoRefreshIntervalSeconds = normalized }
            });
            RestartAutoRefresh();
        }
    }

    private string _nextAutoRefreshText = string.Empty;

    public string NextAutoRefreshText
    {
        get => _nextAutoRefreshText;
        private set => SetProperty(ref _nextAutoRefreshText, value);
    }

    private void RestartAutoRefresh()
    {
        CancelAndDispose(ref _autoRefreshCancellation);
        NextAutoRefreshText = string.Empty;
        if (!_isPageActive || !IsHistoryAutoRefreshEnabled || IsDisposed)
        {
            return;
        }

        var cancellationToken = ReplaceCancellationSource(ref _autoRefreshCancellation);
        RunFireAndForget(RunAutoRefreshAsync(cancellationToken), nameof(RunAutoRefreshAsync), _logger);
    }

    private async Task RunAutoRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var delay = CreateAutoRefreshDelay(
                    HistoryAutoRefreshIntervalSeconds,
                    RandomNumberGenerator.GetInt32(-100, 101));
                NextAutoRefreshText = string.Format(
                    CultureInfo.CurrentCulture,
                    _nextRefreshFormat,
                    delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                await UpdateHistoryMediaListAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    internal static decimal NormalizeAutoRefreshInterval(decimal seconds)
    {
        if (seconds <= 0)
        {
            return ApplicationSettingsDefaults.DefaultHistoryAutoRefreshIntervalSeconds;
        }

        return Math.Max(
            ApplicationSettingsDefaults.MinimumHistoryAutoRefreshIntervalSeconds,
            Math.Round(seconds, 2, MidpointRounding.AwayFromZero));
    }

    internal static TimeSpan CreateAutoRefreshDelay(decimal seconds, int jitterHundredths)
    {
        if (jitterHundredths is < -100 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterHundredths));
        }

        var actualSeconds = NormalizeAutoRefreshInterval(seconds) + jitterHundredths / 100m;
        return TimeSpan.FromMilliseconds((double)(actualSeconds * 1000m));
    }
}
