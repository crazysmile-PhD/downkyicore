using System.Text.Json;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task FlushAsyncPersistsChangesToTheOwnedSettingsFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-settings-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            var manager = new SettingsManager(settingsPath);
            var store = new SettingsStore(manager);

            manager.SetThemeMode(ThemeMode.Dark);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            await using var stream = File.OpenRead(settingsPath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: TestContext.Current.CancellationToken);
            var persistedTheme = document.RootElement
                .GetProperty("Basic")
                .GetProperty("ThemeMode")
                .GetInt32();

            Assert.Equal((int)ThemeMode.Dark, persistedTheme);
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
