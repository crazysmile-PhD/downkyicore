using System.Globalization;
using System.Reflection;

namespace DownKyi.TestInfrastructure;

public sealed class TestDataIsolationFixture : IAsyncDisposable
{
    private const int DeleteAttempts = 4;
    private const string LifecycleMarkerEnvironmentVariable = "DOWNKYI_LIFECYCLE_MARKER";
    private readonly string _root;
    private readonly string? _lifecycleMarker;

    public TestDataIsolationFixture()
    {
        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";
        _root = Path.Combine(
            Path.GetTempPath(),
            "downkyi-tests",
            assemblyName,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("DOWNKYI_DATA_DIR", _root);
        _lifecycleMarker = Environment.GetEnvironmentVariable(LifecycleMarkerEnvironmentVariable);
        WriteLifecycleMarker("started");
    }

    public async ValueTask DisposeAsync()
    {
        WriteLifecycleMarker("disposing");
        for (var attempt = 1; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                DeleteRoot();
                WriteLifecycleMarker("disposed");
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt)).ConfigureAwait(false);
            }
        }

        DeleteRoot();
        WriteLifecycleMarker("disposed");
    }

    private void DeleteRoot()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteLifecycleMarker(string state)
    {
        if (string.IsNullOrWhiteSpace(_lifecycleMarker))
        {
            return;
        }

        var markerDirectory = Path.GetDirectoryName(_lifecycleMarker);
        if (!string.IsNullOrWhiteSpace(markerDirectory))
        {
            Directory.CreateDirectory(markerDirectory);
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{state}|{Environment.ProcessId}|{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        File.AppendAllText(_lifecycleMarker, line + Environment.NewLine);
    }
}
