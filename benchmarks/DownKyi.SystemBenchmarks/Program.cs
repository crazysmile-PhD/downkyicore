using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DownKyi.SystemBenchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var quick = args.Contains("--quick", StringComparer.Ordinal);
        var selectedScenario = ReadOption(args, "--scenario") ?? "all";
        var output = ReadOption(args, "--output")
            ?? Path.Combine("BenchmarkDotNet.Artifacts", "system-benchmark.json");
        var dataRoot = Path.Combine(
            Path.GetTempPath(),
            "downkyi-system-benchmarks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable("DOWNKYI_DATA_DIR", dataRoot);

        try
        {
            var environment = await BenchmarkMetadata.CaptureAsync(cancellation.Token).ConfigureAwait(false);
            var results = string.Equals(selectedScenario, "all", StringComparison.Ordinal)
                ? await RunAllIsolatedAsync(quick, dataRoot, cancellation.Token).ConfigureAwait(false)
                : await RunSelectedAsync(selectedScenario, quick, dataRoot, cancellation.Token).ConfigureAwait(false);

            if (results.Count == 0)
            {
                throw new InvalidOperationException($"Unknown system benchmark scenario: {selectedScenario}");
            }

            var report = new SystemBenchmarkReport(1, DateTimeOffset.UtcNow, environment, results);
            var fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            await File.WriteAllTextAsync(
                fullOutput,
                JsonSerializer.Serialize(report, SystemBenchmarkJsonContext.Default.SystemBenchmarkReport),
                new UTF8Encoding(false),
                cancellation.Token).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(fullOutput).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or JsonException
            or OperationCanceledException)
        {
            await Console.Error
                .WriteLineAsync($"System benchmark failed: {exception.GetType().Name}: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await DeleteDataRootAsync(dataRoot).ConfigureAwait(false);
        }
    }

    private static async Task<List<SystemBenchmarkResult>> RunAllIsolatedAsync(
        bool quick,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        string[] scenarios = ["shell", "ui", "restore", "sqlite", "transfer", "ffmpeg"];
        var results = new List<SystemBenchmarkResult>();
        foreach (var scenario in scenarios)
        {
            var report = await RunIsolatedAsync(
                scenario,
                quick,
                Path.Combine(dataRoot, $"{scenario}.json"),
                cancellationToken).ConfigureAwait(false);
            results.AddRange(report.Results);
        }

        return results;
    }

    private static async Task<SystemBenchmarkReport> RunIsolatedAsync(
        string scenario,
        bool quick,
        string reportPath,
        CancellationToken cancellationToken)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the system benchmark executable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(reportPath);
        if (quick)
        {
            startInfo.ArgumentList.Add("--quick");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the {scenario} benchmark process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var diagnostic = string.IsNullOrWhiteSpace(standardError)
                ? standardOutput.Trim()
                : standardError.Trim();
            throw new InvalidOperationException(
                $"The {scenario} benchmark failed with exit code {process.ExitCode}: {diagnostic}");
        }

        using var stream = File.OpenRead(reportPath);
        return await JsonSerializer.DeserializeAsync(
                stream,
                SystemBenchmarkJsonContext.Default.SystemBenchmarkReport,
                cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {scenario} benchmark produced an empty report.");
    }

    private static async Task<List<SystemBenchmarkResult>> RunSelectedAsync(
        string selectedScenario,
        bool quick,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var results = new List<SystemBenchmarkResult>();
        if (IsSelected(selectedScenario, "shell") || IsSelected(selectedScenario, "ui"))
        {
            var uiHost = new HeadlessAvaloniaHost();
            await using var uiHostScope = uiHost.ConfigureAwait(false);
            if (IsSelected(selectedScenario, "shell"))
            {
                results.Add(await ShellStartupScenario.RunAsync(
                    uiHost,
                    Path.Combine(dataRoot, "shell"),
                    quick ? 2 : 5,
                    cancellationToken).ConfigureAwait(false));
            }

            if (IsSelected(selectedScenario, "ui"))
            {
                results.Add(await UiProgressNotificationScenario.RunAsync(
                    uiHost,
                    sourceSamplesPerSecond: 1000,
                    cancellationToken).ConfigureAwait(false));
            }
        }

        if (IsSelected(selectedScenario, "restore"))
        {
            results.Add(await DownloadRestoreScenario.RunAsync(
                Path.Combine(dataRoot, "restore"),
                quick ? 50 : 1000,
                cancellationToken).ConfigureAwait(false));
        }

        if (IsSelected(selectedScenario, "sqlite"))
        {
            results.Add(await SqliteProgressScenario.RunAsync(
                Path.Combine(dataRoot, "progress"),
                quick ? 4 : 16,
                quick ? 5 : 60,
                samplesPerTaskSecond: 20,
                cancellationToken).ConfigureAwait(false));
        }

        if (IsSelected(selectedScenario, "transfer"))
        {
            results.Add(await TransferThroughputScenario.RunAsync(
                Path.Combine(dataRoot, "transfer"),
                quick ? 2L * 1024 * 1024 : 32L * 1024 * 1024,
                cancellationToken).ConfigureAwait(false));
        }

        if (IsSelected(selectedScenario, "ffmpeg"))
        {
            results.AddRange(await FfmpegConcurrencyScenario.RunAsync(
                quick ? 2 : 8,
                quick ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                quick ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool IsSelected(string selectedScenario, string scenario)
    {
        return string.Equals(selectedScenario, scenario, StringComparison.Ordinal);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task DeleteDataRootAsync(string dataRoot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            }
        }
    }
}
