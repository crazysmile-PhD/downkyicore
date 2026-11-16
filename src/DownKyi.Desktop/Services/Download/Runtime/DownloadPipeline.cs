using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPipeline : IDownloadTaskExecutor
{
    private readonly DownloadExecutionContextFactory _contextFactory;
    private readonly IReadOnlyList<IDownloadPipelineStage> _stages;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadTaskFileService _fileService;
    private readonly DownloadTaskShutdownRecovery _shutdownRecovery;
    private readonly ILogger _logger;

    public DownloadPipeline(
        DownloadExecutionContextFactory contextFactory,
        IReadOnlyList<IDownloadPipelineStage> stages,
        DownloadTaskStateWriter stateWriter,
        DownloadTaskFileService fileService,
        DownloadTaskShutdownRecovery shutdownRecovery,
        ILogger logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _shutdownRecovery = shutdownRecovery
            ?? throw new ArgumentNullException(nameof(shutdownRecovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        var context = _contextFactory.Create(taskId);
        try
        {
            var (stageResult, failedStage) = await ExecuteStagesAsync(
                _stages,
                context,
                cancellationToken).ConfigureAwait(true);
            if (stageResult.IsSuccess)
            {
                return;
            }

            _logger.LogWarningMessage(
                $"Download stage {failedStage ?? "unknown"} failed with " +
                $"{stageResult.Error?.Code ?? "unknown"}.");
            var failure = DownloadActivityPresenter.CreateFailure(stageResult.Error);
            if (stageResult.Error?.Kind == OperationErrorKind.Validation)
            {
                var cleanup = await _fileService.DeleteUnpublishedArtifactsAsync(
                    taskId,
                    cancellationToken).ConfigureAwait(true);
                if (cleanup.Succeeded)
                {
                    await _stateWriter.DiscardUnpublishedArtifactsAndFailAsync(
                        taskId,
                        failure,
                        cancellationToken).ConfigureAwait(true);
                    return;
                }

                _logger.LogWarningMessage(
                    "Invalid unpublished download artifacts could not be removed; delivery remains blocked.");
            }

            await _stateWriter.FailAsync(taskId, failure, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebugMessage(exception.Message);
        }
    }

    public Task MarkFailedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _stateWriter.FailIfRunnableAsync(
            taskId,
            DownloadActivityPresenter.CreateRetryableFailure(),
            cancellationToken);
    }

    public Task PersistShutdownStateAsync()
    {
        return _shutdownRecovery.PersistAsync();
    }

    internal static async Task<DownloadStageRunResult> ExecuteStagesAsync(
        IReadOnlyList<IDownloadPipelineStage> stages,
        DownloadExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var stage in stages)
        {
            var result = await stage.ExecuteAsync(context, cancellationToken)
                .ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                return new DownloadStageRunResult(result, stage.Name);
            }
        }

        return new DownloadStageRunResult(
            DownloadStageResult.Success(nameof(DownloadPipeline)),
            FailedStage: null);
    }

}
