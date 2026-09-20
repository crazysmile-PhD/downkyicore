using System.Diagnostics;
using DownKyi.CentralTestRunner;

namespace DownKyi.Windows.Tests;

public sealed class WindowsProcessSnapshotTests
{
    [Fact]
    public async Task TimeoutBoundsTheSynchronousHelper()
    {
        var clock = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => ProcessTreeSnapshot.ReadWindowsParentIdsAsync(
                TimeSpan.FromMilliseconds(100),
                CreateHoldingFixtureStartInfo)).ConfigureAwait(true);

        Assert.Contains("bounded cleanup window", exception.Message, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private static ProcessStartInfo CreateHoldingFixtureStartInfo()
    {
        var runtimeConfig = Path.Combine(
            AppContext.BaseDirectory,
            $"{Path.GetFileNameWithoutExtension(typeof(WindowsProcessSnapshotTests).Assembly.Location)}.runtimeconfig.json");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add("fixture-hold");
        return startInfo;
    }
}
