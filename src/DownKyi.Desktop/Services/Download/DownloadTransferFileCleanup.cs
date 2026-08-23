using System;
using System.IO;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal static class DownloadTransferFileCleanup
{
    private const int DeleteAttempts = 3;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public static DownloadTransferFileCleanupResult DeleteInvalidArtifacts(
        string? file,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(file))
        {
            return new DownloadTransferFileCleanupResult(0, 0);
        }

        var attemptedCount = 0;
        var failedCount = 0;
        foreach (var path in new[] { file, $"{file}.aria2", $"{file}.download" })
        {
            attemptedCount++;
            try
            {
                if (Directory.Exists(path))
                {
                    failedCount++;
                    logger.LogDebugMessage(
                        "Delete invalid transfer artifact failed; error=unexpected-directory.");
                    break;
                }

                File.Delete(path);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    failedCount++;
                    logger.LogDebugMessage(
                        "Delete invalid transfer artifact failed; error=still-present.");
                    break;
                }
            }
            catch (IOException)
            {
                failedCount++;
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=io.");
                break;
            }
            catch (UnauthorizedAccessException)
            {
                failedCount++;
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=access-denied.");
                break;
            }
        }

        return new DownloadTransferFileCleanupResult(attemptedCount, failedCount);
    }

    public static async Task<DownloadTransferFileCleanupResult> DeleteInvalidArtifactsAsync(
        string? file,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var result = new DownloadTransferFileCleanupResult(0, 0);
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = DeleteInvalidArtifacts(file, logger);
            if (result.Succeeded)
            {
                return result;
            }

            if (attempt < DeleteAttempts)
            {
                await Task.Delay(
                    DeleteRetryDelay,
                    timeProvider,
                    cancellationToken).ConfigureAwait(true);
            }
        }

        return result;
    }
}

internal sealed record DownloadTransferFileCleanupResult(
    int AttemptedCount,
    int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
