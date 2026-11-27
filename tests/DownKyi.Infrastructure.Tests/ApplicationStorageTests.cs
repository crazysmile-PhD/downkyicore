using DownKyi.Infrastructure.Logging;
using DownKyi.Infrastructure.Storage;

namespace DownKyi.Infrastructure.Tests;

public sealed class ApplicationStorageTests
{
    [Fact]
    public async Task GeneralMaintenancePreservesLogsAndDiagnosticBundlesOwnedByLogging()
    {
        var now = DateTimeOffset.UtcNow;
        var logs = ApplicationStorage.GetLogsDir();
        var activePath = Path.Combine(logs, $"active-{Guid.NewGuid():N}.log");
        var retention = new ApplicationLogRetentionManager(new ApplicationLogOptions(logs));
        var bundle = retention.ReserveDiagnosticDirectory(now);
        var eventsPath = Path.Combine(bundle, "events.jsonl");
        var manifestPath = Path.Combine(bundle, "manifest.json");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(activePath, "active", cancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(eventsPath, "events", cancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(manifestPath, "manifest", cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(activePath, now.AddDays(-40).UtcDateTime);
            File.SetLastWriteTimeUtc(eventsPath, now.AddDays(-40).UtcDateTime);

            retention.Apply(activePath, now);
            await ApplicationStorage.RunMaintenanceAsync(cancellationToken).ConfigureAwait(true);

            Assert.True(File.Exists(activePath));
            Assert.True(File.Exists(eventsPath));
            Assert.True(File.Exists(manifestPath));
        }
        finally
        {
            File.Delete(activePath);
            retention.ReleaseDiagnosticDirectory(bundle, delete: true);
        }
    }

    [Fact]
    public void MigratedStoragePreservesTheConfiguredDataLayout()
    {
        // The assembly fixture supplies a private root before storage is initialized.
        var configuredRoot = Environment.GetEnvironmentVariable("DOWNKYI_DATA_DIR");
        Assert.False(string.IsNullOrWhiteSpace(configuredRoot));
        var root = Path.GetFullPath(configuredRoot);

        Assert.Equal(root, ApplicationStorage.GetRoot());
        Assert.Equal(Path.Combine(root, "Config", "Settings"), ApplicationStorage.GetSettings());
        Assert.Equal(Path.Combine(root, "Config", "Login"), ApplicationStorage.GetLogin());
        Assert.Equal(Path.Combine(root, "Storage", "Data.db"), ApplicationStorage.GetDbPath());
        Assert.Equal(Path.Combine(root, "Storage", "Download.db"), ApplicationStorage.GetDownload());
        Assert.Equal(Path.Combine(root, "Media"), ApplicationStorage.GetMedia());
        Assert.Equal(Path.Combine(root, "Cache"), ApplicationStorage.GetCache());
        Assert.Equal(Path.Combine(root, "Logs"), ApplicationStorage.GetLogsDir());
        Assert.Equal(Path.Combine(root, "Aria"), ApplicationStorage.GetAriaDir());
        foreach (var name in new[] { "Config", "Storage", "Media", "Cache", "Logs", "Aria" })
        {
            Assert.True(Directory.Exists(Path.Combine(root, name)));
        }
    }
}
