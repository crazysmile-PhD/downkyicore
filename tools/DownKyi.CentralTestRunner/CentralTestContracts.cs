using System.Collections.ObjectModel;

#pragma warning disable CA1515 // PowerShell compatibility wrappers invoke this compiled boundary.

namespace DownKyi.CentralTestRunner;

public sealed class CentralTestProjectOptions
{
    public CentralTestProjectOptions(
        string repositoryRoot,
        string projectPath,
        string configuration,
        bool noRestore,
        bool noBuild,
        string? resultsDirectory,
        string? trxName,
        IEnumerable<string>? classNames,
        string? filter,
        int executionTimeoutSeconds)
        : this(
            repositoryRoot,
            projectPath,
            configuration,
            noRestore,
            noBuild,
            resultsDirectory,
            trxName,
            classNames,
            filter,
            executionTimeoutSeconds,
            environmentVariables: null)
    {
    }

    public CentralTestProjectOptions(
        string repositoryRoot,
        string projectPath,
        string configuration,
        bool noRestore,
        bool noBuild,
        string? resultsDirectory,
        string? trxName,
        IEnumerable<string>? classNames,
        string? filter,
        int executionTimeoutSeconds,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentOutOfRangeException.ThrowIfLessThan(executionTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(executionTimeoutSeconds, 3600);

        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        ProjectPath = Path.GetFullPath(projectPath, RepositoryRoot);
        Configuration = configuration;
        NoRestore = noRestore;
        NoBuild = noBuild;
        ResultsDirectory = string.IsNullOrWhiteSpace(resultsDirectory)
            ? null
            : Path.GetFullPath(resultsDirectory, RepositoryRoot);
        TrxName = trxName;
        ClassNames = new ReadOnlyCollection<string>(
            (classNames ?? []).Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
        Filter = filter;
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in environmentVariables ?? new Dictionary<string, string?>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            if (pair.Key.StartsWith("DOWNKYI_CENTRAL_TEST_", StringComparison.Ordinal) ||
                pair.Key is "DOWNKYI_LIFECYCLE_MARKER" or "DOWNKYI_LIFECYCLE_MARKER_OWNER")
            {
                throw new InvalidOperationException(
                    $"The central runner owns reserved environment variable '{pair.Key}'.");
            }
            if (!environment.TryAdd(pair.Key, pair.Value))
            {
                throw new InvalidOperationException(
                    $"Duplicate child environment variable '{pair.Key}'.");
            }
        }
        EnvironmentVariables = new ReadOnlyDictionary<string, string?>(environment);
    }

    public string RepositoryRoot { get; }

    public string ProjectPath { get; }

    public string Configuration { get; }

    public bool NoRestore { get; }

    public bool NoBuild { get; }

    public string? ResultsDirectory { get; }

    public string? TrxName { get; }

    public IReadOnlyList<string> ClassNames { get; }

    public string? Filter { get; }

    public int ExecutionTimeoutSeconds { get; }

    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }
}

public sealed record CentralTestRunResult(
    int ExitCode,
    string Runner,
    string? TrxPath,
    CentralTestExecutionReport Report,
    DownKyi.ProcessSupervision.ProcessOwnershipMetadata Ownership);

public sealed class CentralTestSolutionOptions
{
    public CentralTestSolutionOptions(
        string repositoryRoot,
        string configuration,
        bool noRestore,
        bool noBuild,
        string? resultsDirectory,
        int executionTimeoutSeconds)
        : this(
            repositoryRoot,
            configuration,
            noRestore,
            noBuild,
            resultsDirectory,
            executionTimeoutSeconds,
            shardIndex: 0,
            shardCount: 1,
            maxParallelProjects: 2)
    {
    }

    public CentralTestSolutionOptions(
        string repositoryRoot,
        string configuration,
        bool noRestore,
        bool noBuild,
        string? resultsDirectory,
        int executionTimeoutSeconds,
        int shardIndex,
        int shardCount,
        int maxParallelProjects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentOutOfRangeException.ThrowIfLessThan(executionTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(executionTimeoutSeconds, 3600);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(shardIndex, shardCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelProjects, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxParallelProjects, 8);
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        Configuration = configuration;
        NoRestore = noRestore;
        NoBuild = noBuild;
        ResultsDirectory = string.IsNullOrWhiteSpace(resultsDirectory)
            ? null
            : Path.GetFullPath(resultsDirectory, RepositoryRoot);
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
        ShardIndex = shardIndex;
        ShardCount = shardCount;
        MaxParallelProjects = maxParallelProjects;
    }

    public string RepositoryRoot { get; }

    public string Configuration { get; }

    public bool NoRestore { get; }

    public bool NoBuild { get; }

    public string? ResultsDirectory { get; }

    public int ExecutionTimeoutSeconds { get; }

    public int ShardIndex { get; }

    public int ShardCount { get; }

    public int MaxParallelProjects { get; }
}

public sealed record CentralTestProjectRunResult(
    string ProjectPath,
    CentralTestRunResult Result);

public sealed record CentralTestSolutionResult(
    string Platform,
    int SelectedProjectCount,
    int ShardIndex,
    int ShardCount,
    IReadOnlyList<CentralTestProjectRunResult> ProjectResults);

public sealed record CentralTestExecutionReport(
    int Executed,
    int ExecutedExpected,
    int ExecutedExpectedClasses,
    int PassedExpected,
    int PassedExpectedClasses,
    int Failed,
    string ReportPath);

internal enum CentralTestValidatorMutation
{
    None,
    TreatProcessExitZeroAsPass,
    AcceptZeroExecuted
}

internal enum CentralTestAuthorizationMutation
{
    None,
    WrongToken,
    WrongInvocationHash,
    Replay,
    Partial
}

[Flags]
internal enum CentralTestRunnerMutation
{
    None = 0,
    FailAuthorizationBeforeCompletion = 1,
    FailExecutionAfterAuthorization = 2,
    FailLeaseCleanup = 4,
    FailTemporaryResultsCleanup = 8
}
