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
    public async Task FlushWritesRedactedMessageExceptionAndScope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider();
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

            var log = await ReadLogFilesAsync(cancellationToken).ConfigureAwait(true);
            Assert.Contains("Download.Runtime", log, StringComparison.Ordinal);
            Assert.Contains("access_key=[redacted]", log, StringComparison.Ordinal);
            Assert.Contains("token=[redacted]", log, StringComparison.Ordinal);
            Assert.Contains("[email]", log, StringComparison.Ordinal);
            Assert.Contains($"[path]{Path.DirectorySeparatorChar}private.mp4", log, StringComparison.Ordinal);
            Assert.Contains("mid=[redacted]", log, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", log, StringComparison.Ordinal);
            Assert.DoesNotContain("scope-secret", log, StringComparison.Ordinal);
            Assert.DoesNotContain("SESSDATA-value", log, StringComparison.Ordinal);
            Assert.DoesNotContain("alice@example.test", log, StringComparison.Ordinal);
            Assert.DoesNotContain("C:\\Users\\alice", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task RecentEventsAreBoundedAndDiagnosticExportUsesThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = CreateProvider(recentEventCapacity: 3);
        try
        {
            var logger = provider.CreateLogger("Recent");
            for (var index = 0; index < 6; index++)
            {
                RecentEntry(logger, index, null);
            }

            var recent = provider.GetRecentEvents();
            Assert.Equal(3, recent.Count);
            Assert.DoesNotContain(recent, entry => entry.Message.Contains("entry=2", StringComparison.Ordinal));
            Assert.Contains(recent, entry => entry.Message.Contains("entry=5", StringComparison.Ordinal));

            var diagnosticPath = await provider.ExportDiagnosticLogAsync(cancellationToken).ConfigureAwait(true);
            var diagnostic = await File.ReadAllTextAsync(diagnosticPath, cancellationToken).ConfigureAwait(true);
            Assert.Contains("entry=3", diagnostic, StringComparison.Ordinal);
            Assert.Contains("entry=5", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("entry=2", diagnostic, StringComparison.Ordinal);
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
        var provider = CreateProvider(maxFileBytes: 320);
        try
        {
            var logger = provider.CreateLogger("Rotation");
            var payload = new string('x', 90);
            for (var index = 0; index < 8; index++)
            {
                RotationEntry(logger, index, payload, null);
            }

            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);

            var files = Directory.GetFiles(_directory, "*.log", SearchOption.TopDirectoryOnly);
            Assert.True(files.Length > 1);
            Assert.All(files, path => Assert.True(new FileInfo(path).Length <= 520, path));
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

        var log = await ReadLogFilesAsync(cancellationToken).ConfigureAwait(true);
        Assert.Contains("final-entry", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRetentionDeletesExpiredAndExcessLogs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        for (var index = 0; index < 4; index++)
        {
            var path = Path.Combine(_directory, $"old-{index}.log");
            await File.WriteAllTextAsync(path, "old", cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30 - index));
        }

        var provider = new ApplicationLogProvider(new ApplicationLogOptions(_directory)
        {
            MaxRetainedAge = TimeSpan.FromDays(7),
            MaxRetainedFiles = 2
        });
        try
        {
            await provider.FlushAsync(cancellationToken).ConfigureAwait(true);
            Assert.Empty(Directory.GetFiles(_directory, "old-*.log", SearchOption.TopDirectoryOnly));
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

    private ApplicationLogProvider CreateProvider(int recentEventCapacity = 20, long maxFileBytes = 1024 * 1024)
    {
        return new ApplicationLogProvider(new ApplicationLogOptions(_directory)
        {
            QueueCapacity = 64,
            RecentEventCapacity = recentEventCapacity,
            MaxFileBytes = maxFileBytes
        });
    }

    private async Task<string> ReadLogFilesAsync(CancellationToken cancellationToken)
    {
        var contents = new List<string>();
        foreach (var path in Directory.GetFiles(_directory, "*.log", SearchOption.TopDirectoryOnly))
        {
            contents.Add(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(true));
        }

        return string.Join(Environment.NewLine, contents);
    }
}
