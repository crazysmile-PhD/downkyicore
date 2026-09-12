using DownKyi.Domain.Downloads;
using DownKyi.Domain.Results;

namespace DownKyi.Application.Downloads;

public sealed partial class DownloadTaskApplicationService
{
    public Task<OperationResult<DownloadTask>> BeginPublishingArtifactAsync(
        DownloadTaskId taskId,
        DownloadPublishingArtifact publishing,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, (task, now) => task.BeginPublishingArtifact(publishing, now), cancellationToken);

    public Task<OperationResult<DownloadTask>> ClearPublishingArtifactAsync(
        DownloadTaskId taskId,
        CancellationToken cancellationToken) =>
        MutateAsync(taskId, static (task, now) => task.ClearPublishingArtifact(now), cancellationToken);
}
