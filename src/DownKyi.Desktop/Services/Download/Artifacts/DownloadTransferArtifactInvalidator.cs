using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadTransferArtifactInvalidator
{
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly ILogger<DownloadTransferArtifactInvalidator> _logger;

    public DownloadTransferArtifactInvalidator(
        DownloadTaskStateWriter stateWriter,
        ILogger<DownloadTransferArtifactInvalidator> logger)
    {
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DownloadTransferArtifactInvalidationOutcome> InvalidateAsync(
        DownloadTaskId taskId,
        string? stagingDirectory,
        IReadOnlyCollection<DownloadTransferArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count == 0)
        {
            return DownloadTransferArtifactInvalidationOutcome.None;
        }

        var cleanedByKey = new Dictionary<string, bool>(StringComparer.Ordinal);
        var cleanupFailed = false;
        foreach (var artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cleanup = DownloadTransferFileCleanup.DeleteInvalidArtifacts(
                artifact.FilePath,
                stagingDirectory,
                _logger);
            cleanupFailed |= !cleanup.Succeeded;
            cleanedByKey[artifact.TransferKey] =
                cleanup.Succeeded &&
                (!cleanedByKey.TryGetValue(artifact.TransferKey, out var prior) || prior);
        }

        var cleanedKeys = cleanedByKey
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToArray();
        if (cleanedKeys.Length > 0)
        {
            await _stateWriter.InvalidateCompletedFilesAsync(
                taskId,
                cleanedKeys,
                cancellationToken).ConfigureAwait(true);
        }

        return cleanupFailed
            ? DownloadTransferArtifactInvalidationOutcome.CleanupFailed
            : DownloadTransferArtifactInvalidationOutcome.Invalidated;
    }
}

internal sealed record DownloadTransferArtifact(string TransferKey, string FilePath);

internal enum DownloadTransferArtifactInvalidationOutcome
{
    None,
    Invalidated,
    CleanupFailed
}
