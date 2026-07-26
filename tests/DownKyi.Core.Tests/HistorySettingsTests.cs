using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class HistorySettingsTests
{
    [Fact]
    public async Task HistoryAutoRefreshSettingsAreNormalizedAndPersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "downkyi-history-settings", Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            using (var store = new SettingsStore(settingsPath))
            {
                var updated = store.Update(settings => settings with
                {
                    History = settings.History with
                    {
                        IsAutoRefreshEnabled = true,
                        AutoRefreshIntervalSeconds = 9.999m
                    }
                });
                Assert.True(updated.History.IsAutoRefreshEnabled);
                Assert.Equal(10m, updated.History.AutoRefreshIntervalSeconds);
                await store.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            using var reopened = new SettingsStore(settingsPath);
            Assert.True(reopened.Current.History.IsAutoRefreshEnabled);
            Assert.Equal(10m, reopened.Current.History.AutoRefreshIntervalSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
