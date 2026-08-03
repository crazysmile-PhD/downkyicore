using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Services.Download;

internal sealed class DownloadPipeline : IDownloadTaskExecutor
{
    private readonly DownloadExecutionContextFactory _contextFactory;
    private readonly IReadOnlyList<IDownloadPipelineStage> _stages;
    private readonly DownloadTaskStateWriter _stateWriter;
    private readonly DownloadTaskShutdownRecovery _shutdownRecovery;
    private readonly ITransferBackend _transferBackend;
    private readonly ILogger _logger;
    private bool _disposed;

    public DownloadPipeline(
        DownloadExecutionContextFactory contextFactory,
        IReadOnlyList<IDownloadPipelineStage> stages,
        DownloadTaskStateWriter stateWriter,
        DownloadTaskShutdownRecovery shutdownRecovery,
        ITransferBackend transferBackend,
        ILogger logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        _stateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
        _shutdownRecovery = shutdownRecovery
            ?? throw new ArgumentNullException(nameof(shutdownRecovery));
        _transferBackend = transferBackend ?? throw new ArgumentNullException(nameof(transferBackend));
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
            await _stateWriter.FailAsync(
                taskId,
                DownloadActivityPresenter.CreateFailure(stageResult.Error),
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogDebugMessage(exception.Message);
        }
    }

    public Task MarkFailedAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return _stateWriter.FailAsync(
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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _transferBackend.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return _transferBackend.StopAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transferBackend.Dispose();
    }
}
