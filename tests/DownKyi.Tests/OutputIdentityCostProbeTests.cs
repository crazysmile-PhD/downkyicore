using System.Diagnostics;
using DownKyi.Application.Downloads;

namespace DownKyi.Tests;

public sealed class OutputIdentityCostProbeTests
{
    [Fact(Explicit = true)]
    public void MeasureReservationIdentityCost()
    {
        const int taskCount = 2048;

        var root =
            Path.Combine(
                Path.GetTempPath(),
                "downkyi-output-identity-cost",
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var requestedBasePath =
                Path.Combine(
                    root,
                    "same-output");

            var resolvedPaths =
                Enumerable
                    .Range(0, taskCount)
                    .Select(index =>
                        index == 0
                            ? requestedBasePath
                            : $"{requestedBasePath}({index})")
                    .ToArray();

            var ignoreCase =
                DownloadOutputPathKey
                    .UsesCaseInsensitiveComparison;

            // Warm-up JIT / path APIs.
            _ = DownloadOutputPathKey.Create(
                requestedBasePath,
                ignoreCase);

            _ = DownloadOutputPathKey.NormalizeLogicalPath(
                requestedBasePath);

            var requestedCreate =
                Measure(
                    () =>
                    {
                        for (var index = 0;
                             index < taskCount;
                             index++)
                        {
                            _ = DownloadOutputPathKey.Create(
                                requestedBasePath,
                                ignoreCase);
                        }
                    });

            var candidateCreate =
                Measure(
                    () =>
                    {
                        foreach (var path in resolvedPaths)
                        {
                            _ = DownloadOutputPathKey.Create(
                                path,
                                ignoreCase);
                        }
                    });

            var plannedCreate =
                Measure(
                    () =>
                    {
                        foreach (var path in resolvedPaths)
                        {
                            _ = DownloadOutputPathKey.Create(
                                path,
                                ignoreCase);
                        }
                    });

            var normalizeOnly =
                Measure(
                    () =>
                    {
                        for (var repeat = 0;
                             repeat < 3;
                             repeat++)
                        {
                            foreach (var path in resolvedPaths)
                            {
                                _ =
                                    DownloadOutputPathKey
                                        .NormalizeLogicalPath(
                                            path);
                            }
                        }
                    });

            var createTotal =
                requestedCreate +
                candidateCreate +
                plannedCreate;

            var reportPath =
                Environment.GetEnvironmentVariable(
                    "DOWNKYI_OUTPUT_IDENTITY_COST_REPORT")
                ?? Path.Combine(
                    root,
                    "output-identity-cost.txt");

            var report =
                new[]
                {
                    "DownKyi output identity cost probe",
                    $"UTC: {DateTimeOffset.UtcNow:O}",
                    "",
                    $"Tasks: {taskCount}",
                    $"Expected Create calls: {taskCount * 3}",
                    "",
                    $"Requested-key Create ms: {requestedCreate:F3}",
                    $"Candidate-key Create ms: {candidateCreate:F3}",
                    $"Planned-key Create ms: {plannedCreate:F3}",
                    $"Create total ms: {createTotal:F3}",
                    $"Normalize-only 6144 ms: {normalizeOnly:F3}",
                    $"Physical-identity overhead ms: {createTotal - normalizeOnly:F3}"
                };

            var reportDirectory =
                Path.GetDirectoryName(reportPath);

            if (!string.IsNullOrEmpty(reportDirectory))
            {
                Directory.CreateDirectory(
                    reportDirectory);
            }

            File.WriteAllLines(
                reportPath,
                report);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static double Measure(Action action)
    {
        var stopwatch =
            Stopwatch.StartNew();

        action();

        stopwatch.Stop();

        return stopwatch
            .Elapsed
            .TotalMilliseconds;
    }
}
