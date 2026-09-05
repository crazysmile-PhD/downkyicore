namespace DownKyi.CentralTestRunner;

internal sealed class RecorderReport
{
    public required string SliceIdentity { get; init; }

    public required string TestIdentity { get; init; }

    public DateTimeOffset RecorderStartedAtUtc { get; init; }

    public RootProcessIdentity? RootProcess { get; set; }

    public string? Outcome { get; set; }

    public required List<RecorderEvent> Events { get; init; }

    public string? StdoutTail { get; set; }

    public string? StderrTail { get; set; }

    public FinalProcessSnapshot? FinalSnapshot { get; set; }

    public string? DiagnosticGuidance { get; set; }
}

internal sealed class RootProcessIdentity
{
    public int Pid { get; init; }

    public DateTimeOffset StartTimeUtc { get; init; }
}

internal sealed class RecorderEvent
{
    public DateTimeOffset TimestampUtc { get; init; }

    public required string Event { get; init; }

    public int? Pid { get; init; }

    public DateTimeOffset? StartTimeUtc { get; init; }

    public int? ExitCode { get; init; }

    public string? Detail { get; init; }
}

internal sealed class FinalProcessSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }

    public required string Completeness { get; init; }

    public required IReadOnlyList<ObservedProcess> Processes { get; init; }

    public string? Error { get; init; }
}

internal sealed class ObservedProcess
{
    public int Pid { get; init; }

    public int ParentPid { get; init; }

    public DateTimeOffset? StartTimeUtc { get; init; }
}
