using System.Diagnostics;
using DownKyi.Composition;
using DownKyi.Core.Logging;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Composition;
using DownKyi.Infrastructure.Downloads;
using DownKyi.ViewModels;
using DownKyi.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DownKyi.SystemBenchmarks;

internal static class ShellStartupScenario
{
    public static async Task<SystemBenchmarkResult> RunAsync(
        HeadlessAvaloniaHost uiHost,
        string dataRoot,
        int warmIterations,
        CancellationToken cancellationToken)
    {
        var cold = await uiHost.RunAsync(() => MeasureOnceAsync(dataRoot, cancellationToken)).ConfigureAwait(false);
        var warmSamples = new List<double>(warmIterations);
        for (var index = 0; index < warmIterations; index++)
        {
            warmSamples.Add(await uiHost
                .RunAsync(() => MeasureOnceAsync(dataRoot, cancellationToken))
                .ConfigureAwait(false));
        }

        warmSamples.Sort();
        return new SystemBenchmarkResult(
            "shell_startup",
            $"empty isolated profile; warm_iterations={warmIterations}",
            "headless-host",
            Available: true,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["cold_milliseconds"] = cold,
                ["warm_median_milliseconds"] = Median(warmSamples),
                ["warm_minimum_milliseconds"] = warmSamples[0]
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cold_milliseconds"] = "ms",
                ["warm_median_milliseconds"] = "ms",
                ["warm_minimum_milliseconds"] = "ms"
            },
            "Cold and warm are measured in one process on one machine; headless platform setup is excluded, while Host composition, MainWindow resolution, and XAML loading are included.");
    }

    private static async Task<double> MeasureOnceAsync(
        string dataRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settingsStore = new SettingsStore(Path.Combine(dataRoot, "settings.json"));
        var logProvider = new ApplicationLogProvider(
            new ApplicationLogOptions(Path.Combine(dataRoot, "logs")));
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var host = DownKyiHost.Create(services =>
            {
                services.AddDownKyiDesktop(loggerFactory, logProvider);
                services.Replace(ServiceDescriptor.Singleton<ISettingsStore>(settingsStore));
                services.Replace(ServiceDescriptor.Singleton(
                    new SqliteDownloadTaskStoreOptions(Path.Combine(dataRoot, "downkyi.db"))));
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            _ = host.Services.GetRequiredService<MainWindowViewModel>();
            if (window.Content == null)
            {
                throw new InvalidOperationException("MainWindow XAML did not load content.");
            }

            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }
        finally
        {
            loggerFactory.Dispose();
            await logProvider.DisposeAsync().ConfigureAwait(false);
            await settingsStore.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static double Median(List<double> values)
    {
        return values.Count % 2 == 0
            ? (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2
            : values[values.Count / 2];
    }
}
