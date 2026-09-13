using DownKyi.Domain.Results;

namespace DownKyi.Domain.Downloads;

public sealed partial class DownloadTask
{
    public OperationResult<DownloadTask> BeginPublishingArtifact(
        DownloadPublishingArtifact publishing,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publishing);
        if (Phase != DownloadPhase.Downloading ||
            (Output.PublishingArtifact != null && Output.PublishingArtifact != publishing))
        {
            return PublishingConflict();
        }

        if (Output.PublishedArtifacts.TryGetValue(publishing.Key, out var existing))
        {
            var destination = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(Output.BasePath))!, publishing.FileName);
            if (!string.Equals(existing, destination, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return PublishingConflict();
            }
        }

        return UpdateOutput(new DownloadOutput(
            Output.BasePath, Output.FileSizeText, Output.PublishedArtifacts,
            Output.StagingToken, publishing), now);
    }

    public OperationResult<DownloadTask> ClearPublishingArtifact(DateTimeOffset now) =>
        Output.PublishingArtifact == null
            ? PublishingConflict()
            : UpdatePublishingOutput(new DownloadOutput(
                Output.BasePath, Output.FileSizeText, Output.PublishedArtifacts,
                Output.StagingToken), now);

    public OperationResult<DownloadTask> RecordPublishedArtifact(
        DownloadPublishingArtifact publishing,
        string path,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(publishing);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Phase is DownloadPhase.Completed or DownloadPhase.Deleted)
        {
            return InvalidTransition(Phase);
        }

        if (Output.PublishingArtifact != publishing ||
            publishing.FileName != Path.GetFileName(path))
        {
            return PublishingConflict();
        }

        if (Output.PublishedArtifacts.TryGetValue(publishing.Key, out var existing) && existing != path)
        {
            return OperationResult.Failure<DownloadTask>(new OperationError(
                "download.output.published-conflict",
                "A published artifact cannot be replaced.",
                OperationErrorKind.Conflict));
        }

        var output = new DownloadOutput(
            Output.BasePath,
            Output.FileSizeText,
            Output.PublishedArtifacts.SetItem(publishing.Key, path),
            Output.StagingToken);
        return UpdatePublishingOutput(output, now);
    }

    private OperationResult<DownloadTask> UpdatePublishingOutput(DownloadOutput output, DateTimeOffset now)
    {
        if (Phase == DownloadPhase.Deleted)
        {
            return InvalidTransition(Phase);
        }

        EnsureTimestampDoesNotMoveBackward(now);
        return OperationResult.Success(new DownloadTask(
            Id, Metadata, Plan, output, Phase, Progress, Transfer, Failure, Completion,
            checked(Version + 1), CreatedAtUtc, now));
    }

    private static OperationResult<DownloadTask> PublishingConflict() =>
        OperationResult.Failure<DownloadTask>(new OperationError(
            "download.output.publishing",
            "A download artifact must finish its pending publication before this transition.",
            OperationErrorKind.Conflict));
}
