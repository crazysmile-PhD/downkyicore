namespace DownKyi.Core.FFmpeg;

internal static class FfmpegInputDiagnostic
{
    private static readonly string[] DecodeCorruptionEvidence =
    [
        "Invalid data found when processing input",
        "Error while decoding stream",
        "corrupt decoded frame",
        "Packet corrupt",
        "Invalid NAL unit",
        "Error splitting the input into NAL units",
        "moov atom not found",
        "partial file"
    ];

    public static IReadOnlyList<FfmpegInputFailure> ProbeInputs(
        IEnumerable<string> inputPaths)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        return inputPaths
            .Select(ProbeInput)
            .Where(failure => failure != null)
            .Cast<FfmpegInputFailure>()
            .ToArray();
    }

    public static async Task<IReadOnlyList<FfmpegInputFailure>> FindInputFailuresAsync(
        IFfmpegProcessRunner processRunner,
        AsyncConcurrencyGate concurrencyGate,
        IEnumerable<string> inputPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(concurrencyGate);
        ArgumentNullException.ThrowIfNull(inputPaths);
        var inputFailures = new List<FfmpegInputFailure>();
        foreach (var inputPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probeFailure = ProbeInput(inputPath);
            if (probeFailure != null)
            {
                inputFailures.Add(probeFailure);
                continue;
            }

            using var slot = await concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var result = await processRunner.RunAsync(
                FfmpegCommandFactory.BuildValidateInput(inputPath),
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (IsConfirmedDecodeCorruption(result))
            {
                inputFailures.Add(new FfmpegInputFailure(
                    inputPath,
                    FfmpegInputFailureKind.DecodeCorruption));
            }
            else if (!result.Succeeded)
            {
                inputFailures.Add(new FfmpegInputFailure(
                    inputPath,
                    ClassifyDiagnosticFailure(result)));
            }
        }

        return inputFailures;
    }

    internal static bool IsConfirmedDecodeCorruption(FfmpegProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !result.Succeeded &&
               result.ProcessStarted &&
               !result.TimedOut &&
               DecodeCorruptionEvidence.Any(evidence =>
                   result.StandardError.Contains(evidence, StringComparison.OrdinalIgnoreCase));
    }

    public static FfmpegOperationFailureKind ClassifyOperationFailure(
        IReadOnlyList<FfmpegInputFailure> inputFailures,
        FfmpegOperationFailureKind fallback)
    {
        ArgumentNullException.ThrowIfNull(inputFailures);
        if (inputFailures.Any(failure => failure.CanInvalidate))
        {
            return FfmpegOperationFailureKind.InvalidInput;
        }

        if (inputFailures.Any(failure => failure.Kind is FfmpegInputFailureKind.Inaccessible
                or FfmpegInputFailureKind.UnsupportedFileType))
        {
            return FfmpegOperationFailureKind.InputAccess;
        }

        if (inputFailures.Any(failure => failure.Kind == FfmpegInputFailureKind.DiagnosticTimeout))
        {
            return FfmpegOperationFailureKind.Timeout;
        }

        return inputFailures.Any(failure =>
            failure.Kind == FfmpegInputFailureKind.DiagnosticUnavailable)
            ? FfmpegOperationFailureKind.ProcessUnavailable
            : fallback;
    }

    private static FfmpegInputFailure? ProbeInput(string inputPath)
    {
        try
        {
            var attributes = File.GetAttributes(inputPath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return new FfmpegInputFailure(
                    inputPath,
                    FfmpegInputFailureKind.UnsupportedFileType);
            }

            return new FileInfo(inputPath).Length == 0
                ? new FfmpegInputFailure(inputPath, FfmpegInputFailureKind.Empty)
                : null;
        }
        catch (FileNotFoundException)
        {
            return new FfmpegInputFailure(inputPath, FfmpegInputFailureKind.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new FfmpegInputFailure(inputPath, FfmpegInputFailureKind.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return new FfmpegInputFailure(inputPath, FfmpegInputFailureKind.Inaccessible);
        }
        catch (IOException)
        {
            return new FfmpegInputFailure(inputPath, FfmpegInputFailureKind.Inaccessible);
        }
    }

    private static FfmpegInputFailureKind ClassifyDiagnosticFailure(
        FfmpegProcessResult result)
    {
        if (!result.ProcessStarted)
        {
            return FfmpegInputFailureKind.DiagnosticUnavailable;
        }

        return result.TimedOut
            ? FfmpegInputFailureKind.DiagnosticTimeout
            : FfmpegInputFailureKind.DiagnosticFailure;
    }
}
