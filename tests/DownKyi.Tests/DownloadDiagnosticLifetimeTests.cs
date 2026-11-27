using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Tests;

public sealed class DownloadDiagnosticLifetimeTests
{
    [Fact]
    public async Task SeparateTransfersDoNotShareSpeedLogThrottleState()
    {
        var directory = Directory.CreateTempSubdirectory("downkyi-diagnostic-lifetime-");
        try
        {
            const string fileName = "completed-media";
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, fileName), [1, 2, 3, 4],
                TestContext.Current.CancellationToken);
            using var settings = new TestSettingsStore();
            var logger = new SpeedLogger();
            var backend = new BuiltinTransferBackend(settings.Store, new DownloadDiagnosticLogger(logger),
                NullLogger<BuiltinTransferBackend>.Instance);
            var request = new DownloadTransferRequest(
                new DownloadTaskId("diagnostic-lifetime"), ["https://unused.invalid/media"],
                directory.FullName, fileName, 4, static () => { }, static () => false,
                static _ => { }, static (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

            Assert.Equal(DownloadTransferOutcome.Succeeded, (await backend.TransferAsync(request)).Outcome);
            Assert.Equal(DownloadTransferOutcome.Succeeded, (await backend.TransferAsync(request)).Outcome);
            Assert.Equal(2, logger.SpeedMessages);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class SpeedLogger : ILogger<DownloadDiagnosticLogger>
    {
        public int SpeedMessages { get; private set; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Download speed", StringComparison.Ordinal)) SpeedMessages++;
        }
    }
}
