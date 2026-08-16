namespace DownKyi.Application.Downloads;

/// <summary>
/// The outcome of attempting to capture publication evidence.
/// </summary>
public enum OutputArtifactEvidenceCaptureStatus
{
    Captured,
    Missing,
    Unsupported,
    Failed
}

/// <summary>
/// The evidence-capture result. Only <see cref="OutputArtifactEvidenceCaptureStatus.Captured"/>
/// with non-null <see cref="Evidence"/> may authorize durable provenance.
/// </summary>
public sealed record OutputArtifactEvidenceCaptureResult(
    OutputArtifactEvidenceCaptureStatus Status,
    OutputArtifactPublicationEvidence? Evidence)
{
    public bool Succeeded =>
        Status == OutputArtifactEvidenceCaptureStatus.Captured
        && Evidence is not null;

    public static OutputArtifactEvidenceCaptureResult Captured(
        OutputArtifactPublicationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new OutputArtifactEvidenceCaptureResult(
            OutputArtifactEvidenceCaptureStatus.Captured,
            evidence);
    }

    public static OutputArtifactEvidenceCaptureResult Missing() =>
        new(OutputArtifactEvidenceCaptureStatus.Missing, null);

    public static OutputArtifactEvidenceCaptureResult Unsupported() =>
        new(OutputArtifactEvidenceCaptureStatus.Unsupported, null);

    public static OutputArtifactEvidenceCaptureResult Failed() =>
        new(OutputArtifactEvidenceCaptureStatus.Failed, null);
}
