using System.Collections.ObjectModel;

namespace DownKyi.Core.FFMpeg;

internal sealed class FfmpegCommand
{
    public FfmpegCommand(string executable, IEnumerable<string> arguments, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        Executable = executable;
        Arguments = new ReadOnlyCollection<string>(arguments.ToArray());
        Operation = operation;
    }

    public string Executable { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string Operation { get; }
}

internal sealed record FfmpegProcessResult(
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public static FfmpegProcessResult Timeout(string standardOutput, string standardError)
    {
        return new FfmpegProcessResult(false, -1, standardOutput, standardError, true);
    }
}

internal interface IFfmpegProcessRunner
{
    Task<FfmpegProcessResult> RunAsync(
        FfmpegCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
