using System.Text.Json;
using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DownKyi.Core.Tests;

public sealed class ApplicationLogProviderTests : IDisposable
{
    private static readonly Action<ILogger, Exception?> RequestFailed = LoggerMessage.Define(
        MicrosoftLogLevel.Error,
        new EventId(1, nameof(RequestFailed)),
        "Request failed for https://example.test/file?access_key=query-secret user=alice@example.test mid=123456");
    private static readonly Action<ILogger, int, Exception?> RecentEntry = LoggerMessage.Define<int>(
        MicrosoftLogLevel.Information,
        new EventId(2, nameof(RecentEntry)),
        "entry={Index}");
    private static readonly Action<ILogger, int, string, Exception?> RotationEntry = LoggerMessage.Define<int, string>(
        MicrosoftLogLevel.Warning,
        new EventId(3, nameof(RotationEntry)),
        "entry={Index} payload={Payload}");
    private static readonly Action<ILogger, Exception?> FinalEntry = LoggerMessage.Define(
        MicrosoftLogLevel.Critical,
        new EventId(4, nameof(FinalEntry)),
        "final-entry");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DownKyi.Core.Tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(@"C:\Users\alice\Videos\private.mp4")]
    [InlineData("/home/alice/Videos/private.mp4")]
    public void RedactorUsesOnlyTheLeafNameForEveryPathStyle(string path)
    {
        var redacted = new SensitiveDataRedactor().Redact(path);

        Assert.Equal($"[path]{Path.DirectorySeparatorChar}private.mp4", redacted);
        Assert.DoesNotContain("alice", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultPolicyMatchesRetentionContract()
    {
        var options = new ApplicationLogOptions(_directory);

        Assert.Equal(32L * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(512L * 1024 * 1024, options.MaxTotalBytes);
        Assert.Equal(TimeSpan.FromDays(7), options.MaxRetainedAge);
        Assert.Equal(TimeSpan.FromHours(1), options.MaintenanceInterval);
    }

    [Fact]
    public async Task FlushWritesRedactedJsonLinesIntoUtcDayDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 3, 4, 5, TimeSpan.Zero));
        var provider = CreateProvider(clock: clock);
        try
        {
            var logger = provider.CreateLogger("Download.Runtime");
            using (logger.BeginScope("task=42 token=scope-secret"))
            {
                RequestFailed(
                    logger,
                    new InvalidOperationException("cookie=SESSDATA-value path=C:\\Users\\alice\\Videos\\private.mp4"));
            }

            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            var path = Assert.Single(GetEventFiles());
            Assert.Equal("2026-07-18", Directory.GetParent(path)?.Name);
            var line = Assert.Single(await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(true));
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            Assert.Equal("2026-07-18T03:04:05+00:00", root.GetProperty("timestamp").GetString());
            Assert.Equal("Download.Runtime", root.GetProperty("category").GetString());
            Assert.Contains("access_key=[redacted]", root.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("token=[redacted]", root.GetProperty("scope").GetString(), StringComparison.Ordinal);
            Assert.Contains($"[path]{Path.DirectorySeparatorChar}private.mp4", root.GetProperty("exceptionText").GetString(), StringComparison.Ordinal);
            var serialized = root.GetRawText();
            Assert.DoesNotContain("query-secret", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("scope-secret", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("SESSDATA-value", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("alice@example.test", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Users\\alice", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DiagnosticExportWritesAiReadableManifestAndBoundedEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 3, 4, 5, TimeSpan.Zero));
        var provider = CreateProvider(recentEventCapacity: 3, clock: clock);
        try
        {
            var logger = provider.CreateLogger("Recent");
            for (var index = 0; index < 6; index++)
            {
                RecentEntry(logger, index, null);
            }

            var manifestPath = await provider.ExportDiagnosticLogAsync(cancellationToken).ConfigureAwait(true);
            using var manifest = JsonDocument.Parse(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(true));
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("applicationVersion").GetString()));
            Assert.Equal(3, manifest.RootElement.GetProperty("eventCount").GetInt32());
            Assert.Equal("events.jsonl", manifest.RootElement.GetProperty("files")[0].GetString());
            Assert.Equal(6, manifest.RootElement.GetProperty("storage").GetProperty("eventsWritten").GetInt64());

            var exportedEvents = await File.ReadAllLinesAsync(
                Path.Combine(Path.GetDirectoryName(manifestPath)!, "events.jsonl"),
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(3, exportedEvents.Length);
            Assert.DoesNotContain(exportedEvents, line => line.Contains("entry=2", StringComparison.Ordinal));
            Assert.Contains(exportedEvents, line => line.Contains("entry=5", StringComparison.Ordinal));
            Assert.All(exportedEvents, line => JsonDocument.Parse(line).Dispose());
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task WriterRotatesBeforeAppendingPastConfiguredFileLimit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider(maxFileBytes: 420);
        try
        {
            var logger = provider.CreateLogger("Rotation");
            var payload = new string('x', 90);
            for (var index = 0; index < 8; index++)
            {
                RotationEntry(logger, index, payload, null);
            }

            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            var files = GetEventFiles();
            Assert.True(files.Length > 1);
            Assert.All(files, path => Assert.True(new FileInfo(path).Length <= 620, path));
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task MaintenanceProtectsActiveFileAndRotationEnforcesCapacity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider(maxFileBytes: 420, maxTotalBytes: 1);
        try
        {
            var logger = provider.CreateLogger("Capacity");
            RotationEntry(logger, 1, new string('x', 90), null);
            await provider.RequestMaintenanceAsync(cancellationToken).ConfigureAwait(true);

            Assert.Single(GetEventFiles());
            Assert.Equal(0, provider.GetMetrics().CapacityDeletionCount);

            RotationEntry(logger, 2, new string('y', 90), null);
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.Single(GetEventFiles());
            Assert.True(provider.GetMetrics().CapacityDeletionCount >= 1);
            Assert.True(provider.GetMetrics().CapacityRatio > 1);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DayChangeCreatesAnotherUtcDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 23, 59, 59, TimeSpan.Zero));
        var provider = CreateProvider(clock: clock);
        try
        {
            var logger = provider.CreateLogger("DayChange");
            RecentEntry(logger, 1, null);
            clock.Advance(TimeSpan.FromSeconds(2));
            RecentEntry(logger, 2, null);
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.Equal(
                ["2026-07-18", "2026-07-19"],
                GetEventFiles().Select(path => Directory.GetParent(path)!.Name).Distinct().Order().ToArray());
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task AsyncDisposalDrainsAcceptedEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider();
        var logger = provider.CreateLogger("Shutdown");
        FinalEntry(logger, null);

        await provider.DisposeAsync().ConfigureAwait(true);

        Assert.Contains(
            "final-entry",
            await ReadEventFilesAsync(cancellationToken).ConfigureAwait(true),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRetentionDeletesExpiredLogsAndReportsMetrics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var oldDirectory = Path.Combine(_directory, "2026-07-01");
        Directory.CreateDirectory(oldDirectory);
        var oldPath = Path.Combine(oldDirectory, "events-000.jsonl");
        await File.WriteAllTextAsync(oldPath, "{}\n", cancellationToken).ConfigureAwait(true);
        File.SetLastWriteTimeUtc(oldPath, clock.GetUtcNow().UtcDateTime.AddDays(-8));

        var provider = CreateProvider(clock: clock);
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.False(File.Exists(oldPath));
            Assert.Equal(1, provider.GetMetrics().AgeDeletionCount);
            Assert.Equal(clock.GetUtcNow(), provider.GetMetrics().LastMaintenanceUtc);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task StartupCapacityRetentionDeletesOldestClosedLogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var dayDirectory = Path.Combine(_directory, "2026-07-18");
        Directory.CreateDirectory(dayDirectory);
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(dayDirectory, $"events-{index:D3}.jsonl");
            await File.WriteAllTextAsync(path, new string('x', 200), cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddMinutes(index));
        }

        var provider = CreateProvider(maxTotalBytes: 250, clock: clock);
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.Single(GetEventFiles());
            Assert.Equal(2, provider.GetMetrics().CapacityDeletionCount);
            Assert.True(provider.GetMetrics().RetainedBytes <= 250);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task StartupRetentionDeletesExpiredDiagnosticBundleAsOneUnit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var diagnosticDirectory = await CreateDiagnosticBundleAsync(
            "diagnostic-20260701T000000000Z",
            clock.GetUtcNow().UtcDateTime.AddDays(-8),
            cancellationToken).ConfigureAwait(true);

        var provider = CreateProvider(clock: clock);
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.False(Directory.Exists(diagnosticDirectory));
            Assert.Equal(1, provider.GetMetrics().AgeDeletionCount);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task CapacityRetentionDeletesWholeDiagnosticBundle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var diagnosticDirectory = await CreateDiagnosticBundleAsync(
            "diagnostic-20260718T000000000Z",
            clock.GetUtcNow().UtcDateTime,
            cancellationToken,
            fileSize: 200).ConfigureAwait(true);

        var provider = CreateProvider(maxTotalBytes: 250, clock: clock);
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.False(Directory.Exists(diagnosticDirectory));
            Assert.Equal(1, provider.GetMetrics().CapacityDeletionCount);
            Assert.Equal(0, provider.GetMetrics().RetainedBytes);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task LoggingAfterMaintenanceIntervalDeletesExpiredClosedFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero));
        var provider = CreateProvider(clock: clock);
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);
            var oldDirectory = Path.Combine(_directory, "2026-07-01");
            Directory.CreateDirectory(oldDirectory);
            var oldPath = Path.Combine(oldDirectory, "events-000.jsonl");
            await File.WriteAllTextAsync(oldPath, "{}\n", cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(oldPath, clock.GetUtcNow().UtcDateTime.AddDays(-8));

            clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
            RecentEntry(provider.CreateLogger("HourlyMaintenance"), 1, null);
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            Assert.False(File.Exists(oldPath));
            Assert.Equal(1, provider.GetMetrics().AgeDeletionCount);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DiagnosticExportsAtSameTimestampUseDifferentDirectories()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider();
        try
        {
            var first = await provider.ExportDiagnosticLogAsync(cancellationToken).ConfigureAwait(true);
            var second = await provider.ExportDiagnosticLogAsync(cancellationToken).ConfigureAwait(true);

            Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FlushReportsWriterInitializationFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        await File.WriteAllTextAsync(_directory, "not-a-directory", cancellationToken).ConfigureAwait(true);
        var provider = CreateProvider();
        try
        {
            await Assert.ThrowsAnyAsync<IOException>(
                () => provider.FlushAsync(cancellationToken)).ConfigureAwait(true);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
        else if (File.Exists(_directory))
        {
            File.Delete(_directory);
        }
    }

    private ApplicationLogProvider CreateProvider(
        int recentEventCapacity = 20,
        long maxFileBytes = 1024 * 1024,
        long maxTotalBytes = 512L * 1024 * 1024,
        ManualTimeProvider? clock = null)
    {
        return new ApplicationLogProvider(
            new ApplicationLogOptions(_directory)
            {
                QueueCapacity = 64,
                RecentEventCapacity = recentEventCapacity,
                MaxFileBytes = maxFileBytes,
                MaxTotalBytes = maxTotalBytes
            },
            new SensitiveDataRedactor(),
            clock ?? new ManualTimeProvider(new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero)));
    }

    private string[] GetEventFiles()
    {
        return Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "events-*.jsonl", SearchOption.AllDirectories)
            : [];
    }

    private async Task<string> ReadEventFilesAsync(CancellationToken cancellationToken)
    {
        var contents = new List<string>();
        foreach (var path in GetEventFiles())
        {
            contents.Add(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true));
        }

        return string.Join(Environment.NewLine, contents);
    }

    private async Task<string> CreateDiagnosticBundleAsync(
        string name,
        DateTime lastWriteTimeUtc,
        CancellationToken cancellationToken,
        int fileSize = 20)
    {
        var directory = Path.Combine(_directory, "Diagnostics", name);
        Directory.CreateDirectory(directory);
        foreach (var fileName in new[] { "events.jsonl", "manifest.json" })
        {
            var path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, new string('x', fileSize), cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        }

        Directory.SetLastWriteTimeUtc(directory, lastWriteTimeUtc);
        return directory;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow += duration;
        }
    }
}
