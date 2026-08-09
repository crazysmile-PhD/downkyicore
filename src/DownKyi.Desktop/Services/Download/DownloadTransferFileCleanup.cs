using System;
using System.IO;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal static class DownloadTransferFileCleanup
{
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
                File.Delete(path);
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
}

internal sealed record DownloadTransferFileCleanupResult(
    int AttemptedCount,
    int FailedCount)
{
    public bool Succeeded => FailedCount == 0;
}
