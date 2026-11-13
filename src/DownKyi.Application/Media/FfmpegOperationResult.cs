namespace DownKyi.Application.Media;

public sealed record FfmpegConcatSegment(int Order, string FilePath);

public enum FfmpegOperationFailureKind
{
    None,
    NoInput,
    ProcessUnavailable,
    ProcessFailure,
    Timeout,
    InputInvalid,
    OutputInvalid,
    DestinationConflict,
    DestinationAccessDenied,
    OutputIoFailure
}

public enum FfmpegOperationFailureStage
{
    None,
    Media,
    Cover
}

public sealed record FfmpegOperationResult
{
    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason)
        : this(
            succeeded,
            outputPath,
            failureReason,
            succeeded ? FfmpegOperationFailureKind.None : FfmpegOperationFailureKind.ProcessFailure,
            succeeded ? FfmpegOperationFailureStage.None : FfmpegOperationFailureStage.Media)
    {
    }

    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason,
        FfmpegOperationFailureKind failureKind)
        : this(
            succeeded,
            outputPath,
            failureReason,
            failureKind,
            succeeded ? FfmpegOperationFailureStage.None : FfmpegOperationFailureStage.Media)
    {
    }

    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason,
        FfmpegOperationFailureKind failureKind,
        FfmpegOperationFailureStage failureStage)
    {
        if (succeeded != (failureKind == FfmpegOperationFailureKind.None)
            || succeeded != (failureStage == FfmpegOperationFailureStage.None))
        {
            throw new ArgumentException(
                "Successful FFmpeg operations require no failure kind or stage; failed operations require both.",
                nameof(failureKind));
        }

        Succeeded = succeeded;
        OutputPath = outputPath;
        FailureReason = failureReason;
        FailureKind = failureKind;
        FailureStage = failureStage;
    }

    public bool Succeeded { get; }

    public string? OutputPath { get; }

    public string? FailureReason { get; }

    public FfmpegOperationFailureKind FailureKind { get; }

    public FfmpegOperationFailureStage FailureStage { get; }

    public static FfmpegOperationResult Failure(string reason)
    {
        return Failure(reason, FfmpegOperationFailureKind.ProcessFailure);
    }

    public static FfmpegOperationResult Failure(
        string reason,
        FfmpegOperationFailureKind failureKind)
    {
        return Failure(reason, failureKind, FfmpegOperationFailureStage.Media);
    }

    public static FfmpegOperationResult Failure(
        string reason,
        FfmpegOperationFailureKind failureKind,
        FfmpegOperationFailureStage failureStage)
    {
        return new FfmpegOperationResult(
            false,
            null,
            reason,
            failureKind,
            failureStage);
    }
}
