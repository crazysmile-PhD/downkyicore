namespace DownKyi.Domain.Downloads;

public sealed record DownloadOutput(string BasePath, string? FileSizeText);

public sealed record DownloadCompletion(
    long FinishedTimestamp,
    string FinishedTimeText,
    string? MaximumSpeedText);

public sealed record DownloadFailure(
    string Code,
    string Message,
    bool IsTransient);
