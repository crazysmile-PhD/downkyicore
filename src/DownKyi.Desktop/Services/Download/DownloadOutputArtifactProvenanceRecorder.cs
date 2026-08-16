using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

/// <summary>
/// Persists final-output provenance after a non-overwriting publication has
/// succeeded. Failure to persist intentionally leaves the output untracked:
/// cleanup must preserve it rather than attempting rollback deletion.
/// </summary>
internal sealed class DownloadOutputArtifactProvenanceRecorder
{
    private readonly IDownloadOutputArtifactProvenanceApplicationService _provenance;
    private readonly ILogger _logger;

    public DownloadOutputArtifactProvenanceRecorder(
        IDownloadOutputArtifactProvenanceApplicationService provenance,
        ILogger logger)
    {
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAfterPublishAsync(
        DownloadTaskId taskId,
        string artifactKey,
        string artifactKind,
        string canonicalPath,
        OutputArtifactPublicationEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);

        if (evidence == null)
        {
            _logger.LogWarningMessage(
                "Final output published without ownership evidence; automatic cleanup will preserve it.");
            return;
        }

        try
        {
            // Publication has already crossed its irreversible filesystem
            // boundary. Persist independently of cancellation; a failure is
            // deliberately represented as untracked output, never rollback.
            var result = await _provenance.RecordPublishedAsync(
                    taskId,
                    artifactKey,
                    artifactKind,
                    canonicalPath,
                    evidence,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarningMessage(
                    "Final output provenance could not be persisted; automatic cleanup will preserve it.");
            }
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or NotSupportedException
                                         or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogErrorMessage(
                "Final output provenance persistence failed; automatic cleanup will preserve it.",
                exception);
        }
    }
}
