using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.FFmpeg;

internal sealed class FfmpegConcatRuntime
{
    private static readonly TimeSpan ConcatTimeout = TimeSpan.FromHours(2);
    private readonly AsyncConcurrencyGate _concurrencyGate;
    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ILogger<FfmpegConcatRuntime> _logger;

    public FfmpegConcatRuntime(
        IFfmpegProcessRunner processRunner,
        AsyncConcurrencyGate concurrencyGate,
        ILogger<FfmpegConcatRuntime> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _concurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FfmpegOperationResult> ConcatAsync(
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputFile,
        FfmpegHardwareEncoderProfile? hardwareEncoder,
        bool allowStreamCopy,
        bool overwriteDestination,
        Action<string>? progress = null,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? coverFile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        if (segments.Count == 0)
        {
            return FfmpegOperationResult.Failure(
                "No input segments were provided.",
                FfmpegOperationFailureKind.NoInput);
        }

        var orderedSegments = segments.OrderBy(segment => segment.Order).ToArray();
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputFile))
            ?? throw new InvalidOperationException("The output directory is unavailable.");
        var listFile = Path.Combine(outputDirectory, $".downkyi-concat-{Guid.NewGuid():N}.txt");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllLinesAsync(
                    listFile,
                    orderedSegments.Select(segment => ToConcatFileLine(segment.FilePath)),
                    cancellationToken)
                .ConfigureAwait(false);

            var operationFailureKind = FfmpegOperationFailureKind.ProcessFailure;
            using (await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var strategy in FfmpegProcessingPlan.BuildConcatPlan(hardwareEncoder, allowStreamCopy))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var temporaryOutput = Path.Combine(
                        outputDirectory,
                        $".{Path.GetFileNameWithoutExtension(outputFile)}-{Guid.NewGuid():N}.partial.mp4");
                    try
                    {
                        progress?.Invoke($"FFmpeg {strategy}");
                        var command = FfmpegCommandFactory.BuildConcat(
                            listFile,
                            temporaryOutput,
                            strategy,
                            hardwareEncoder,
                            embeddedAudioMode,
                            coverFile);
                        var processResult = await _processRunner
                            .RunAsync(command, ConcatTimeout, cancellationToken)
                            .ConfigureAwait(false);
                        LogProcessResult(command, processResult);
                        if (!processResult.Succeeded)
                        {
                            operationFailureKind = !processResult.ProcessStarted
                                ? FfmpegOperationFailureKind.ProcessUnavailable
                                : processResult.TimedOut
                                    ? FfmpegOperationFailureKind.Timeout
                                    : FfmpegOperationFailureKind.ProcessFailure;
                            continue;
                        }

                        if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
                        {
                            _logger.LogInformationMessage("FFmpeg did not produce a non-empty output.");
                            operationFailureKind = FfmpegOperationFailureKind.OutputInvalid;
                            continue;
                        }

                        File.Move(temporaryOutput, outputFile, overwrite: overwriteDestination);
                        return new FfmpegOperationResult(
                            true,
                            outputFile,
                            null,
                            FfmpegOperationFailureKind.None);
                    }
                    finally
                    {
                        DeleteFile(temporaryOutput);
                    }
                }
            }

            return FfmpegOperationResult.Failure(
                "All FFmpeg concat strategies failed or produced an empty output.",
                operationFailureKind);
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("FFmpeg concat output could not be finalized.", e);
            return FfmpegOperationResult.Failure(
                "FFmpeg output could not be finalized.",
                !overwriteDestination && File.Exists(outputFile)
                    ? FfmpegOperationFailureKind.DestinationConflict
                    : FfmpegOperationFailureKind.OutputIoFailure);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("FFmpeg concat output finalization was denied.", e);
            return FfmpegOperationResult.Failure(
                "FFmpeg output could not be finalized.",
                FfmpegOperationFailureKind.DestinationAccessDenied);
        }
        finally
        {
            DeleteFile(listFile);
        }
    }

    private static string ToConcatFileLine(string file)
    {
        var normalizedPath = Path.GetFullPath(file).Replace('\\', '/');
        return $"file '{normalizedPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private void LogProcessResult(FfmpegCommand command, FfmpegProcessResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? "none"
            : result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown";
        _logger.LogInformationMessage(
            $"FFmpeg operation completed. operation={command.Operation}; exitCode={result.ExitCode}; timedOut={result.TimedOut}; error={error}");
    }

    private void DeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (IOException e)
        {
            _logger.LogDebugMessage($"FFmpeg cleanup failed: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogDebugMessage($"FFmpeg cleanup was denied: {e.Message}");
        }
    }
}
