using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Infrastructure.Downloads;

internal static class DownloadStoreOperationResults
{
    public static OperationResult Conflict(DownloadTaskId taskId, string reason)
    {
        return OperationResult.Failure(new OperationError(
            "download.store.conflict",
            $"Download task '{taskId.Value}' {reason}.",
            OperationErrorKind.Conflict));
    }

    public static OperationResult NotFound(DownloadTaskId taskId)
    {
        return OperationResult.Failure(new OperationError(
            "download.store.not_found",
            $"Download task '{taskId.Value}' was not found.",
            OperationErrorKind.NotFound));
    }

    public static OperationResult OutputPathConflict()
    {
        return OperationResult.Failure(new OperationError(
            "download.store.output_path_reserved",
            "The selected output path is already reserved by another active download.",
            OperationErrorKind.Conflict));
    }
}
