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

    public static async Task<IReadOnlyList<string>> FindInvalidInputsAsync(
        IFfmpegProcessRunner processRunner,
        AsyncConcurrencyGate concurrencyGate,
        IEnumerable<string> inputPaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(concurrencyGate);
        ArgumentNullException.ThrowIfNull(inputPaths);
        var invalidInputs = new List<string>();
        foreach (var inputPath in inputPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(inputPath) || new FileInfo(inputPath).Length == 0)
            {
                invalidInputs.Add(inputPath);
                continue;
            }

            using var slot = await concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var result = await processRunner.RunAsync(
                FfmpegCommandFactory.BuildValidateInput(inputPath),
                timeout,
                cancellationToken).ConfigureAwait(false);
            if (IsConfirmedDecodeCorruption(result))
            {
                invalidInputs.Add(inputPath);
            }
        }

        return invalidInputs;
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
}
