using System.Threading;
using System.Threading.Tasks;
using DownKyi.Domain.Results;

namespace DownKyi.Services.Download;

internal interface IDownloadPipelineStage
{
    string Name { get; }

    Task<OperationResult<DownloadStageResult>> ExecuteAsync(
        DownloadExecutionContext context,
        CancellationToken cancellationToken);
}

internal sealed record DownloadStageResult(string StageName)
{
    public static OperationResult<DownloadStageResult> Success(string stageName)
    {
        return OperationResult.Success(new DownloadStageResult(stageName));
    }

    public static OperationResult<DownloadStageResult> Failure(
        string code,
        string message,
        OperationErrorKind kind = OperationErrorKind.Unexpected)
    {
        return OperationResult.Failure<DownloadStageResult>(
            new OperationError(code, message, kind));
    }
}

internal sealed record DownloadStageRunResult(
    OperationResult<DownloadStageResult> Result,
    string? FailedStage);
