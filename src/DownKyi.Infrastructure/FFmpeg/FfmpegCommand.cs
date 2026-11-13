using System.Collections.ObjectModel;
using DownKyi.Application.Media;

namespace DownKyi.Infrastructure.FFmpeg;

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
    bool TimedOut,
    bool ProcessStarted = true)
{
    private static readonly string[] InvalidInputIndicators =
    [
        "Invalid data found when processing input",
        "Error while decoding stream",
        "matches no streams"
    ];

    public static FfmpegProcessResult Timeout(string standardOutput, string standardError)
    {
        return new FfmpegProcessResult(false, -1, standardOutput, standardError, true);
    }

    public FfmpegOperationFailureKind ToOperationFailureKind()
    {
        if (Succeeded)
        {
            return FfmpegOperationFailureKind.None;
        }

        if (!ProcessStarted)
        {
            return FfmpegOperationFailureKind.ProcessUnavailable;
        }

        if (TimedOut)
        {
            return FfmpegOperationFailureKind.Timeout;
        }

        return InvalidInputIndicators.Any(indicator =>
            StandardError.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            ? FfmpegOperationFailureKind.InputInvalid
            : FfmpegOperationFailureKind.ProcessFailure;
    }
}

internal interface IFfmpegProcessRunner
{
    Task<FfmpegProcessResult> RunAsync(
        FfmpegCommand command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
