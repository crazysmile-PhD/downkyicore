using System.Buffers;
using System.Collections.ObjectModel;

#pragma warning disable CA1515 // PowerShell compatibility wrappers invoke this compiled boundary.

namespace DownKyi.CentralTestRunner;

public sealed class CentralTestProjectOptions
{
    private static readonly SearchValues<char> InvalidTrxNameCharacters =
        SearchValues.Create(Path.GetInvalidFileNameChars().Concat("/\\:").ToArray());

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
        TrxName = ValidateTrxName(trxName);
        ClassNames = new ReadOnlyCollection<string>(
            (classNames ?? []).Order(StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray());
        Filter = filter;
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
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

    private static string? ValidateTrxName(string? trxName)
    {
        if (string.IsNullOrWhiteSpace(trxName))
        {
            return null;
        }

        if (Path.IsPathRooted(trxName) ||
            trxName is "." or ".." ||
            trxName.AsSpan().ContainsAny(InvalidTrxNameCharacters) ||
            !string.Equals(Path.GetFileName(trxName), trxName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The TRX name must be a file name without a root or directory components.",
                nameof(trxName));
        }

        return trxName;
    }
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        ArgumentOutOfRangeException.ThrowIfLessThan(executionTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(executionTimeoutSeconds, 3600);
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
        Configuration = configuration;
        NoRestore = noRestore;
        NoBuild = noBuild;
        ResultsDirectory = string.IsNullOrWhiteSpace(resultsDirectory)
            ? null
            : Path.GetFullPath(resultsDirectory, RepositoryRoot);
        ExecutionTimeoutSeconds = executionTimeoutSeconds;
    }

    public string RepositoryRoot { get; }

    public string Configuration { get; }

    public bool NoRestore { get; }

    public bool NoBuild { get; }

    public string? ResultsDirectory { get; }

    public int ExecutionTimeoutSeconds { get; }
}

public sealed record CentralTestSolutionResult(
    string Platform,
    int SelectedProjectCount,
    IReadOnlyList<CentralTestRunResult> ProjectResults);

public sealed record CentralTestExecutionReport(
    int Executed,
    int ExecutedExpected,
    int ExecutedExpectedClasses,
    int PassedExpected,
    int PassedExpectedClasses,
    int Failed,
    string? ReportPath);

internal enum CentralTestAuthorizationMutation
{
    None,
    WrongToken
}

[Flags]
internal enum CentralTestRunnerMutation
{
    None = 0,
    FailAuthorizationBeforeCompletion = 1
}
