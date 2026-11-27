using System.Globalization;
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
        Func<CancellationToken, Task<FfmpegHardwareEncoderProfile?>>? resolveHardwareEncoder,
        bool allowStreamCopy,
        bool overwriteDestination,
        Action<string>? progress = null,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? coverFile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        cancellationToken.ThrowIfCancellationRequested();
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
            using var slot = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var canStreamCopy = false;
            if (allowStreamCopy)
            {
                var inspection = await new FfmpegConcatInputInspector(_processRunner)
                    .InspectAsync(orderedSegments, embeddedAudioMode, cancellationToken).ConfigureAwait(false);
                if (inspection.Status == FfmpegConcatInputStatus.Failed)
                {
                    return FfmpegOperationResult.Failure(
                        "A media segment could not be inspected.", inspection.FailureKind);
                }

                canStreamCopy = inspection.Status == FfmpegConcatInputStatus.Compatible;
            }

            var hardwareEncoder = !canStreamCopy && resolveHardwareEncoder != null
                ? await resolveHardwareEncoder(cancellationToken).ConfigureAwait(false)
                : null;
            await File.WriteAllLinesAsync(
                    listFile,
                    orderedSegments.SelectMany(ToConcatFileLines),
                    cancellationToken)
                .ConfigureAwait(false);

            var operationFailureKind = FfmpegOperationFailureKind.ProcessFailure;
            FfmpegConcatStrategy[] strategies = (canStreamCopy, hardwareEncoder != null) switch
            {
                (true, _) => [FfmpegConcatStrategy.StreamCopy],
                (false, true) => [FfmpegConcatStrategy.HardwareEncoder, FfmpegConcatStrategy.CpuEncoder],
                _ => [FfmpegConcatStrategy.CpuEncoder]
            };
            foreach (var strategy in strategies)
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
                        operationFailureKind = processResult.ToOperationFailureKind();
                        if (operationFailureKind != FfmpegOperationFailureKind.EncoderUnavailable)
                        {
                            break;
                        }

                        continue;
                    }

                    if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
                    {
                        _logger.LogInformationMessage("FFmpeg did not produce a non-empty output.");
                        operationFailureKind = FfmpegOperationFailureKind.OutputInvalid;
                        break;
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

    private static IEnumerable<string> ToConcatFileLines(FfmpegConcatSegment segment)
    {
        var normalizedPath = Path.GetFullPath(segment.FilePath).Replace('\\', '/');
        yield return $"file '{normalizedPath.Replace("'", "'\\''", StringComparison.Ordinal)}'";
        if (segment.ExpectedDuration is { } duration && duration > TimeSpan.Zero)
        {
            yield return "duration " + duration.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture);
        }
    }

    private void LogProcessResult(FfmpegCommand command, FfmpegProcessResult result)
    {
        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? "none"
            : result.StandardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "unknown";
        _logger.LogInformationMessage(
            $"FFmpeg operation completed. operation={command.Operation}; exitCode={result.ExitCode}; timedOut={result.TimedOut}; error={error}");
        if (!result.Succeeded)
        {
            _logger.LogErrorMessage(
                $"FFmpeg operation failed. operation={command.Operation}; exit={result.ExitCode}; timedOut={result.TimedOut}");
        }
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
