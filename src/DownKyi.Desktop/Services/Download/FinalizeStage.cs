using System;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using DownKyi.Models;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class FinalizeStage : IDownloadPipelineStage
{
    private readonly DownloadTaskProjectionStore _projectionStore;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadCompletionProjector _completionProjector;
    private readonly DownloadTaskFileService _fileService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FinalizeStage> _logger;

    public FinalizeStage(
        DownloadTaskProjectionStore projectionStore,
        DownloadTaskStateWriter stateWriter,
        DownloadCompletionProjector completionProjector,
        DownloadTaskFileService fileService,
        TimeProvider timeProvider,
        ILogger<FinalizeStage> logger)
    {
        _projectionStore = projectionStore
            ?? throw new ArgumentNullException(nameof(projectionStore));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _completionProjector = completionProjector
            ?? throw new ArgumentNullException(nameof(completionProjector));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => nameof(FinalizeStage);

    public async Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureActive(cancellationToken);
        var downloaded = CreateDownloadedSummary(
            _projectionStore
                .GetRequiredSnapshot(context.TaskId)
                .Transfer
                .MaximumBytesPerSecond,
            _timeProvider);

        var completedTask = await _stateWriter.CompleteAsync(
            context.TaskId,
            new DownloadCompletion(
                downloaded.FinishedTimestamp,
                downloaded.FinishedTime,
                downloaded.MaxSpeedDisplay),
            cancellationToken).ConfigureAwait(true);
        try
        {
            await _completionProjector.ProjectAsync(context, completedTask).ConfigureAwait(true);
        }
        finally
        {
            var cleanup = await _fileService.DeleteTransferFilesAsync(
                context.GetMediaInputFiles(),
                CancellationToken.None).ConfigureAwait(true);
            if (!cleanup.Succeeded)
            {
                _logger.LogWarningMessage(
                    $"Committed download input cleanup was incomplete. failedCount={cleanup.FailedCount}.");
            }
        }

        return DownloadStageResult.Success(Name);
    }

    internal static Downloaded CreateDownloadedSummary(
        long maximumBytesPerSecond,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var downloaded = new Downloaded
        {
            MaxSpeedDisplay = Format.FormatSpeedWithBandwidth(maximumBytesPerSecond)
        };
        downloaded.SetFinishedTimestamp(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        return downloaded;
    }
}
