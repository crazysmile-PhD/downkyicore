using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DownKyi.Application.Diagnostics;
using DownKyi.Core.Utils;
using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;
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
        DownloadActionResultEvaluator.Evaluate(context);
        if (!context.ActionResults.HasSucceeded)
        {
            return DownloadStageResult.Failure(
                "download.finalize.no-output",
                "A download cannot complete without a successful requested action.",
                OperationErrorKind.NotFound);
        }

        if (context.NeedsMedia && !context.HasPublished("media"))
        {
            var published = await _fileService.PublishAsync(
                context,
                "media",
                context.OutputMedia ?? throw new InvalidOperationException("Validated media is unavailable."),
                cancellationToken).ConfigureAwait(true);
            if (!published.IsSuccess)
            {
                return OperationResult.Failure<DownloadStageResult>(published.Error!);
            }
        }

        var completion = CreateCompletionSummary(
            _projectionStore
                .GetRequiredSnapshot(context.TaskId)
                .Transfer
                .MaximumBytesPerSecond,
            _timeProvider);

        var completedTask = await _stateWriter.CompleteAsync(
            context.TaskId,
            completion,
            cancellationToken).ConfigureAwait(true);
        try
        {
            await _completionProjector.ProjectAsync(context, completedTask).ConfigureAwait(true);
        }
        finally
        {
            _fileService.CleanupStaging(context.TaskId);
        }

        return DownloadStageResult.Success(Name);
    }

    internal static DownloadCompletion CreateCompletionSummary(
        long maximumBytesPerSecond,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var finishedTimestamp = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var epoch = TimeZoneInfo.ConvertTimeFromUtc(
            new DateTime(1970, 1, 1),
            TimeZoneInfo.Local);
        var finishedTime = epoch.AddSeconds(finishedTimestamp)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return new DownloadCompletion(
            finishedTimestamp,
            finishedTime,
            Format.FormatSpeedWithBandwidth(maximumBytesPerSecond));
    }
}
