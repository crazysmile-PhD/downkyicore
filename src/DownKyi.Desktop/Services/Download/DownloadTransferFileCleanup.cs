using System;
using System.IO;
using DownKyi.Application.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal static class DownloadTransferFileCleanup
{
    public static void DeleteInvalidArtifacts(string? file, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        foreach (var path in new[] { file, $"{file}.aria2", $"{file}.download" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=io.");
            }
            catch (UnauthorizedAccessException)
            {
                logger.LogDebugMessage(
                    "Delete invalid transfer artifact failed; error=access-denied.");
            }
        }
    }
}
