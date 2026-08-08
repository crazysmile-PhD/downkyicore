using System;
using System.IO;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal static class DownloadTransferFileCleanup
{
    private const int DeleteAttempts = 3;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(100);

    public static bool DeleteInvalidArtifacts(string? file, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(file))
        {
            return true;
        }

        var allDeleted = true;
        foreach (var path in new[] { file, $"{file}.aria2", $"{file}.download" })
        {
            try
            {
                if (Directory.Exists(path))
                {
                    logger.LogDebugMessage(
                        "Delete invalid transfer artifact failed; error=unexpected-directory.");
                    allDeleted = false;
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                allDeleted &= !File.Exists(path) && !Directory.Exists(path);
            }
            catch (IOException)
            {
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=io.");
                allDeleted = false;
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=access-denied.");
                allDeleted = false;
            }
        }

        return allDeleted;
    }

    public static async Task<bool> DeleteInvalidArtifactsAsync(
        string? file,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteInvalidArtifacts(file, logger))
            {
                return true;
            }

            if (attempt < DeleteAttempts)
            {
                await Task.Delay(
                    DeleteRetryDelay,
                    timeProvider,
                    cancellationToken).ConfigureAwait(true);
            }
        }

        return false;
    }
}
