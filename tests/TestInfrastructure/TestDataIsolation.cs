using System.Runtime.CompilerServices;

namespace DownKyi.TestInfrastructure;

internal static class TestDataIsolation
{
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "downkyi-tests",
        typeof(TestDataIsolation).Assembly.GetName().Name ?? "unknown",
        Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DOWNKYI_DATA_DIR", Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteRoot();
    }

    private static void TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Process exit is best effort; resource-specific tests detect retained handles.
        }
    }
}
