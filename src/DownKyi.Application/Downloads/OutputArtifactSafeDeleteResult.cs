namespace DownKyi.Application.Downloads;

/// <summary>
/// The fail-closed outcome of an ownership-validated output deletion attempt.
/// </summary>
public enum OutputArtifactSafeDeleteStatus
{
    Deleted,
    Missing,
    Replaced,
    Modified,
    Unsupported,
    Unproven,
    Failed
}

/// <summary>
/// The outcome of an ownership-validated deletion attempt.
/// </summary>
public sealed record OutputArtifactSafeDeleteResult(
    OutputArtifactSafeDeleteStatus Status)
{
    public bool Deleted => Status == OutputArtifactSafeDeleteStatus.Deleted;

    public static OutputArtifactSafeDeleteResult DeletedResult() =>
        new(OutputArtifactSafeDeleteStatus.Deleted);

    public static OutputArtifactSafeDeleteResult Missing() =>
        new(OutputArtifactSafeDeleteStatus.Missing);

    public static OutputArtifactSafeDeleteResult Replaced() =>
        new(OutputArtifactSafeDeleteStatus.Replaced);

    public static OutputArtifactSafeDeleteResult Modified() =>
        new(OutputArtifactSafeDeleteStatus.Modified);

    public static OutputArtifactSafeDeleteResult Unsupported() =>
        new(OutputArtifactSafeDeleteStatus.Unsupported);

    public static OutputArtifactSafeDeleteResult Unproven() =>
        new(OutputArtifactSafeDeleteStatus.Unproven);

    public static OutputArtifactSafeDeleteResult Failed() =>
        new(OutputArtifactSafeDeleteStatus.Failed);
}
