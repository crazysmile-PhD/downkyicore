using DownKyi.Core.Logging;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.FFMpeg;

public sealed record FfmpegConcatSegment(int Order, string FilePath, TimeSpan ExpectedDuration);

public sealed record FfmpegOperationResult(
    bool Succeeded,
    string? OutputPath,
    string? FailureReason,
    TimeSpan Duration)
{
    public static FfmpegOperationResult Failure(string reason)
    {
        return new FfmpegOperationResult(false, null, reason, TimeSpan.Zero);
    }
}

internal sealed class FfmpegConcatRuntime
{
    private static readonly TimeSpan ConcatTimeout = TimeSpan.FromHours(2);
    private readonly AsyncConcurrencyGate _concurrencyGate;
    private readonly IFfmpegMediaValidator _mediaValidator;
    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ILogger<FfmpegConcatRuntime> _logger;

    public FfmpegConcatRuntime(
        IFfmpegProcessRunner processRunner,
        IFfmpegMediaValidator mediaValidator,
        Func<int> maxConcurrencyProvider,
        ILogger<FfmpegConcatRuntime> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _mediaValidator = mediaValidator ?? throw new ArgumentNullException(nameof(mediaValidator));
        _concurrencyGate = new AsyncConcurrencyGate(maxConcurrencyProvider);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FfmpegOperationResult> ConcatAsync(
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputFile,
        FfmpegHardwareEncoderProfile? hardwareEncoder,
        bool allowStreamCopy,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        if (segments.Count == 0)
        {
            return FfmpegOperationResult.Failure("No input segments were provided.");
        }

        var orderedSegments = segments.OrderBy(segment => segment.Order).ToArray();
        if (orderedSegments.Any(segment => !File.Exists(segment.FilePath)))
        {
            return FfmpegOperationResult.Failure("One or more input segments are missing.");
        }

        var expectedDuration = TimeSpan.FromTicks(orderedSegments.Sum(segment => segment.ExpectedDuration.Ticks));
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

            using var slot = await _concurrencyGate.EnterAsync(cancellationToken).ConfigureAwait(false);
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
                        hardwareEncoder);
                    var processResult = await _processRunner
                        .RunAsync(command, ConcatTimeout, cancellationToken)
                        .ConfigureAwait(false);
                    LogProcessResult(command, processResult);
                    if (!processResult.Succeeded)
                    {
                        continue;
                    }

                    var validation = await _mediaValidator
                        .ValidateAsync(temporaryOutput, expectedDuration, cancellationToken)
                        .ConfigureAwait(false);
                    if (!validation.IsValid)
                    {
                        _logger.LogInformationMessage($"FFmpeg output rejected. reason={validation.FailureReason}");
                        continue;
                    }

                    File.Move(temporaryOutput, outputFile, overwrite: true);
                    DeleteSourceSegments(orderedSegments);
                    return new FfmpegOperationResult(true, outputFile, null, validation.Duration);
                }
                finally
                {
                    DeleteFile(temporaryOutput);
                }
            }

            DeleteFile(outputFile);
            return FfmpegOperationResult.Failure("All FFmpeg concat strategies failed validation.");
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("FFmpeg concat output could not be finalized.", e);
            DeleteFile(outputFile);
            return FfmpegOperationResult.Failure("FFmpeg output could not be finalized.");
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("FFmpeg concat output finalization was denied.", e);
            DeleteFile(outputFile);
            return FfmpegOperationResult.Failure("FFmpeg output could not be finalized.");
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

    private void DeleteSourceSegments(IEnumerable<FfmpegConcatSegment> segments)
    {
        foreach (var segment in segments)
        {
            DeleteFile(segment.FilePath);
            DeleteFile($"{segment.FilePath}.aria2");
            DeleteFile($"{segment.FilePath}.download");
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
