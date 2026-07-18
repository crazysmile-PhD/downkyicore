using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.SystemBenchmarks;

internal static class DownloadBenchmarkData
{
    public static DownloadTask CreateDownloadingTask(int index, DateTimeOffset createdAtUtc)
    {
        var identifier = $"benchmark-{index:D6}";
        var task = DownloadTask.Create(
            new DownloadTaskId(identifier),
            new DownloadTaskMetadata(
                new DownloadMediaIdentity("BV1BENCHMARK", index + 1, index + 2, -1, 1, index + 1),
                "Benchmark collection",
                identifier,
                "10:00",
                "AVC",
                new DownloadQuality(80, "1080P"),
                new DownloadQuality(30280, "192K"),
                "redacted-cover",
                "redacted-page-cover",
                0),
            new DownloadPlan(
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["video"] = true },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["video"] = "video.m4s" },
                streamType: 1),
            new DownloadOutput(identifier, "64 MiB"),
            createdAtUtc);
        return task.Start(createdAtUtc.AddTicks(1)).RequireValue();
    }
}
