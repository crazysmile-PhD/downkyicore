using System.Runtime.ExceptionServices;

namespace DownKyi.Tests;

internal sealed record Aria2TlsStageFailure(
    string Stage,
    ExceptionDispatchInfo DispatchInfo)
{
    public Exception Exception => DispatchInfo.SourceException;
}

internal sealed class Aria2TlsMultipleFailuresException : AggregateException
{
    public Aria2TlsMultipleFailuresException()
    {
        Failures = [];
    }

    public Aria2TlsMultipleFailuresException(string message)
        : base(message)
    {
        Failures = [];
    }

    public Aria2TlsMultipleFailuresException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failures =
        [
            new Aria2TlsStageFailure(
                "unspecified",
                ExceptionDispatchInfo.Capture(innerException))
        ];
    }

    public Aria2TlsMultipleFailuresException(IReadOnlyList<Aria2TlsStageFailure> failures)
        : base(CreateMessage(failures), GetExceptions(failures))
    {
        Failures = failures.ToArray();
    }

    public IReadOnlyList<Aria2TlsStageFailure> Failures { get; }

    public Aria2TlsStageFailure PrimaryFailure => Failures[0];

    private static string CreateMessage(IReadOnlyList<Aria2TlsStageFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count < 2)
        {
            throw new ArgumentException(
                "Multiple aria2 TLS failures require at least two stage failures.",
                nameof(failures));
        }

        return $"Multiple aria2 TLS test stages failed. Primary stage: '{failures[0].Stage}'. "
               + $"Failure stages: {string.Join(", ", failures.Select(failure => failure.Stage))}.";
    }

    private static Exception[] GetExceptions(
        IReadOnlyList<Aria2TlsStageFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Select(failure => failure.Exception).ToArray();
    }
}

internal sealed class Aria2TlsFailureCollector
{
    private readonly List<Aria2TlsStageFailure> _failures = [];

    public IReadOnlyList<Aria2TlsStageFailure> Failures => _failures;

    public void Capture(string stage, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is Aria2TlsMultipleFailuresException multipleFailures)
        {
            _failures.AddRange(multipleFailures.Failures);
            return;
        }

        _failures.Add(new Aria2TlsStageFailure(
            stage,
            ExceptionDispatchInfo.Capture(exception)));
    }

    public void Run(string stage, Action action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(action);
        var exception = Record.Exception(action);
        if (exception != null)
        {
            Capture(stage, exception);
        }
    }

    public async Task RunAsync(string stage, Func<Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(action);
        var exception = await Record.ExceptionAsync(action).ConfigureAwait(false);
        if (exception != null)
        {
            Capture(stage, exception);
        }
    }

    public void ThrowIfAny()
    {
        if (_failures.Count == 0)
        {
            return;
        }

        if (_failures.Count == 1)
        {
            _failures[0].DispatchInfo.Throw();
        }

        throw new Aria2TlsMultipleFailuresException(_failures);
    }
}
