using DownKyi.Application.Settings;
using DownKyi.Infrastructure.Settings;
using DownKyi.Platform;

namespace DownKyi.Desktop.Tests;

public sealed class ClipboardMonitorTests
{
    [AvaloniaFact]
    public async Task DisabledMonitoringDoesNotReadAndDropsAnInFlightResult()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-clipboard-");
        try
        {
            var settings = new SettingsStore(Path.Combine(directory.FullName, "settings.json"));
            await using var settingsLifetime = settings.ConfigureAwait(true);
            settings.Update(current => current with
            {
                Basic = current.Basic with { IsListenClipboard = AllowStatus.No }
            });
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            using var monitor = new AvaloniaClipboardMonitor(settings, () =>
            {
                reads++;
                if (reads == 2)
                {
                    secondStarted.SetResult();
                    return pending.Task;
                }
                if (reads == 3) thirdStarted.SetResult();
                return Task.FromResult<string?>("baseline");
            });
            var changes = 0;
            EventHandler<ClipboardTextChangedEventArgs> onChanged = (_, _) => changes++;
            monitor.Changed += onChanged;
            var cancellationToken = TestContext.Current.CancellationToken;
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken).ConfigureAwait(true);
            Assert.Equal(0, reads);

            monitor.SetEnabled(true);
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            monitor.SetEnabled(false);
            pending.SetResult("changed while disabled");
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken).ConfigureAwait(true);
            Assert.Equal(2, reads);
            Assert.Equal(0, changes);

            monitor.SetEnabled(true);
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(true);
            Assert.Equal(0, changes);
            monitor.Changed -= onChanged;
            await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellationToken).ConfigureAwait(true);
            Assert.Equal(3, reads);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
