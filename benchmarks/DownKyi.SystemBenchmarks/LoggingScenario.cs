using System.Diagnostics;
using DownKyi.Application.Diagnostics;
using DownKyi.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.SystemBenchmarks;

internal static class LoggingScenario
{
    private static readonly Action<ILogger, int, Exception?> WriteEntry =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(1, nameof(WriteEntry)),
            "benchmark-event={Index}");

    public static async Task<SystemBenchmarkResult> RunAsync(
        string dataRoot,
        int eventCount,
        CancellationToken cancellationToken)
    {
        var logDirectory = Path.Combine(dataRoot, "logs");
        var provider = new ApplicationLogProvider(
            new ApplicationLogOptions(logDirectory)
            {
                QueueCapacity = 2048,
                RecentEventCapacity = 300,
                MaxFileBytes = long.MaxValue,
                MaxTotalBytes = long.MaxValue
            });
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = loggerFactory.CreateLogger("DownKyi.SystemBenchmarks.Logging");

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var producer = Stopwatch.StartNew();
        for (var index = 0; index < eventCount; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            WriteEntry(logger, index, null);
        }

        producer.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        var flush = Stopwatch.StartNew();
        await provider.FlushAsync(cancellationToken).ConfigureAwait(false);
        flush.Stop();
        var metrics = provider.GetMetrics();
        await provider.DisposeAsync().ConfigureAwait(false);

        var producerSeconds = Math.Max(producer.Elapsed.TotalSeconds, double.Epsilon);
        return new SystemBenchmarkResult(
            "logging",
            $"{eventCount} redacted structured events; queue=2048; recent=300",
            "nlog-6.1.4-jsonl",
            Available: true,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["sourceEvents"] = eventCount,
                ["eventsWritten"] = metrics.EventsWritten,
                ["droppedEvents"] = metrics.DroppedEntries,
                ["producerMilliseconds"] = producer.Elapsed.TotalMilliseconds,
                ["flushMilliseconds"] = flush.Elapsed.TotalMilliseconds,
                ["producerEventsPerSecond"] = eventCount / producerSeconds,
                ["allocatedBytes"] = allocatedBytes,
                ["allocatedBytesPerSourceEvent"] = (double)allocatedBytes / eventCount,
                ["bytesWritten"] = metrics.BytesWritten
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sourceEvents"] = "count",
                ["eventsWritten"] = "count",
                ["droppedEvents"] = "count",
                ["producerMilliseconds"] = "ms",
                ["flushMilliseconds"] = "ms",
                ["producerEventsPerSecond"] = "events/s",
                ["allocatedBytes"] = "bytes",
                ["allocatedBytesPerSourceEvent"] = "bytes/event",
                ["bytesWritten"] = "bytes"
            },
            "Burst producer and explicit flush use the production provider; no threshold is enforced.");
    }
}
