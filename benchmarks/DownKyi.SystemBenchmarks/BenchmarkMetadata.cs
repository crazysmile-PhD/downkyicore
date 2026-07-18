using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DownKyi.SystemBenchmarks;

internal static class BenchmarkMetadata
{
    public static async Task<BenchmarkEnvironment> CaptureAsync(CancellationToken cancellationToken)
    {
        return new BenchmarkEnvironment(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            await ReadCommitShaAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<string> ReadCommitShaAsync(CancellationToken cancellationToken)
    {
        var ciSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(ciSha))
        {
            return ciSha.Trim();
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return "unknown";
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? output.Trim()
                : "unknown";
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
