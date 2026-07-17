using DownKyi.Core.Settings;

namespace DownKyi.Tests;

internal sealed class TestSettingsStore : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-test-settings-{Guid.NewGuid():N}");

    public TestSettingsStore()
    {
        Store = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    public ISettingsStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
