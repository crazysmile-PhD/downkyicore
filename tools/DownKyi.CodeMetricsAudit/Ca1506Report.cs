namespace DownKyi.CodeMetricsAudit;

internal sealed record Ca1506Finding(
    string Rule,
    string Scope,
    string Classification,
    string Project,
    string File,
    int Line,
    int Column,
    string Message,
    string Rationale);

internal sealed record Ca1506Summary(
    int Total,
    int Production,
    int Test,
    IReadOnlyDictionary<string, int> Classifications);

internal sealed record Ca1506Report(
    int SchemaVersion,
    string Rule,
    string Commit,
    bool DirtyWorktree,
    Ca1506Summary Summary,
    IReadOnlyList<Ca1506Finding> Findings);

internal sealed record ProductionClassification(
    string File,
    string Identity,
    string Classification,
    string Rationale);
