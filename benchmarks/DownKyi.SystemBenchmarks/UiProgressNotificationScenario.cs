using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;

namespace DownKyi.SystemBenchmarks;

internal static class UiProgressNotificationScenario
{
    public static Task<SystemBenchmarkResult> RunAsync(
        HeadlessAvaloniaHost uiHost,
        int sourceSamplesPerSecond,
        CancellationToken cancellationToken)
    {
        return uiHost.RunAsync(() => Task.FromResult(Run(sourceSamplesPerSecond, cancellationToken)));
    }

    private static SystemBenchmarkResult Run(
        int sourceSamplesPerSecond,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var updater = new DownloadProgressUiUpdater(
            clock,
            DownloadProgressUiUpdater.DefaultMinimumInterval);
        var item = new DownloadingItem { Downloading = new Downloading() };
        var notificationCount = 0;
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DownloadingItem.Progress)
                or nameof(DownloadingItem.DownloadingFileSize)
                or nameof(DownloadingItem.SpeedDisplay))
            {
                notificationCount++;
            }
        };

        var interval = TimeSpan.FromSeconds(1d / sourceSamplesPerSecond);
        var publishedUpdates = 0;
        for (var sample = 0; sample < sourceSamplesPerSecond; sample++)
        {
            if (updater.TryUpdate(
                    item,
                    sample * 100d / sourceSamplesPerSecond,
                    sample,
                    sourceSamplesPerSecond,
                    1_000_000))
            {
                publishedUpdates++;
            }

            clock.Advance(interval);
        }

        updater.TryUpdate(item, 100, sourceSamplesPerSecond, sourceSamplesPerSecond, 1_000_000);
        publishedUpdates++;
        return new SystemBenchmarkResult(
            "ui_progress_notifications",
            $"source_samples_per_second={sourceSamplesPerSecond}; observed_properties=3",
            "built-in-progress-projection",
            Available: true,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["source_samples_per_second"] = sourceSamplesPerSecond,
                ["published_updates_per_second"] = publishedUpdates,
                ["property_notifications_per_second"] = notificationCount
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source_samples_per_second"] = "samples/s",
                ["published_updates_per_second"] = "updates/s",
                ["property_notifications_per_second"] = "notifications/s"
            },
            "The deterministic one-second window invokes the same updater used by the built-in backend and counts real DownloadingItem PropertyChanged events; completion is always published.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
