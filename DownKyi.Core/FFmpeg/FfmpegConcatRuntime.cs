using DownKyi.Application.Diagnostics;
using DownKyi.Application.Downloads;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.FFmpeg;

public sealed record FfmpegConcatSegment(int Order, string FilePath, TimeSpan ExpectedDuration);

public enum FfmpegOperationFailureKind
{
    None,
    NoInput,
    InvalidInput,
    InputAccess,
    ProcessUnavailable,
    ProcessFailure,
    Timeout,
    OutputInvalid,
    DestinationConflict,
    DestinationAccessDenied,
    OutputIoFailure
}

public enum FfmpegInputFailureKind
{
    Missing,
    Empty,
    DecodeCorruption,
    Inaccessible,
    UnsupportedFileType,
    DiagnosticUnavailable,
    DiagnosticTimeout,
    DiagnosticFailure
}

public sealed record FfmpegInputFailure(string Path, FfmpegInputFailureKind Kind)
{
    public bool CanInvalidate => Kind is FfmpegInputFailureKind.Missing
        or FfmpegInputFailureKind.Empty
        or FfmpegInputFailureKind.DecodeCorruption;
}

public sealed record FfmpegOperationResult
{
    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason,
        TimeSpan duration)
        : this(
            succeeded,
            outputPath,
            failureReason,
            duration,
            succeeded ? FfmpegOperationFailureKind.None : FfmpegOperationFailureKind.ProcessFailure,
            [])
    {
    }

    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason,
        TimeSpan duration,
        IReadOnlyList<string> invalidInputPaths)
        : this(
            succeeded,
            outputPath,
            failureReason,
            duration,
            GetCompatibilityFailureKind(succeeded, invalidInputPaths),
            CreateCompatibilityInputFailures(invalidInputPaths))
    {
    }

    public FfmpegOperationResult(
        bool succeeded,
        string? outputPath,
        string? failureReason,
        TimeSpan duration,
        FfmpegOperationFailureKind failureKind,
        IReadOnlyList<FfmpegInputFailure> inputFailures,
        OutputArtifactPublicationEvidence? publicationEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(inputFailures);
        if (succeeded != (failureKind == FfmpegOperationFailureKind.None))
        {
            throw new ArgumentException(
                "Successful FFmpeg operations require a None failure kind and failed operations require a non-None kind.",
                nameof(failureKind));
        }

        if (!succeeded && publicationEvidence is not null)
        {
            throw new ArgumentException(
                "Failed FFmpeg operations cannot expose publication evidence.",
                nameof(publicationEvidence));
        }

        Succeeded = succeeded;
        OutputPath = outputPath;
        FailureReason = failureReason;
        Duration = duration;
        FailureKind = failureKind;
        PublicationEvidence = publicationEvidence;
        InputFailures = inputFailures.ToArray();
        InvalidInputPaths = InputFailures
            .Where(failure => failure.CanInvalidate)
            .Select(failure => failure.Path)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public bool Succeeded { get; }

    public string? OutputPath { get; }

    public string? FailureReason { get; }

    public TimeSpan Duration { get; }

    public FfmpegOperationFailureKind FailureKind { get; }

    /// <summary>
    /// Evidence captured from the temporary media output and verified against
    /// the object at its final path after publication. It is null when either
    /// step was unavailable or did not succeed; callers must not infer
    /// ownership from the output path.
    /// </summary>
    public OutputArtifactPublicationEvidence? PublicationEvidence { get; }

    public IReadOnlyList<FfmpegInputFailure> InputFailures { get; }

    public IReadOnlyList<string> InvalidInputPaths { get; }

    private static FfmpegOperationFailureKind GetCompatibilityFailureKind(
        bool succeeded,
        IReadOnlyList<string> invalidInputPaths)
    {
        ArgumentNullException.ThrowIfNull(invalidInputPaths);
        return invalidInputPaths.Count > 0
            ? FfmpegOperationFailureKind.InvalidInput
            : succeeded
                ? FfmpegOperationFailureKind.None
                : FfmpegOperationFailureKind.ProcessFailure;
    }

    private static FfmpegInputFailure[] CreateCompatibilityInputFailures(
        IReadOnlyList<string> invalidInputPaths)
    {
        ArgumentNullException.ThrowIfNull(invalidInputPaths);
        return invalidInputPaths.Select(path => new FfmpegInputFailure(
            path,
            FfmpegInputFailureKind.DecodeCorruption)).ToArray();
    }

    public static FfmpegOperationResult Failure(string reason)
    {
        return Failure(reason, FfmpegOperationFailureKind.ProcessFailure);
    }

    public static FfmpegOperationResult Failure(
        string reason,
        IReadOnlyList<string> invalidInputPaths)
    {
        return new FfmpegOperationResult(
            false,
            null,
            reason,
            TimeSpan.Zero,
            invalidInputPaths);
    }

    public static FfmpegOperationResult Failure(
        string reason,
        FfmpegOperationFailureKind failureKind,
        IReadOnlyList<FfmpegInputFailure>? inputFailures = null)
    {
        return new FfmpegOperationResult(
            false,
            null,
            reason,
            TimeSpan.Zero,
            failureKind,
            inputFailures ?? []);
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
        AsyncConcurrencyGate concurrencyGate,
        ILogger<FfmpegConcatRuntime> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _mediaValidator = mediaValidator ?? throw new ArgumentNullException(nameof(mediaValidator));
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
        IOutputArtifactOwnershipProvider? outputArtifactOwnershipProvider = null,
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
        var preflightFailures = FfmpegInputDiagnostic.ProbeInputs(
            orderedSegments.Select(segment => segment.FilePath));
        if (preflightFailures.Count > 0)
        {
            return FfmpegOperationResult.Failure(
                preflightFailures.Any(failure => failure.CanInvalidate)
                    ? "One or more input segments are missing or empty."
                    : "One or more input segments could not be accessed.",
                FfmpegInputDiagnostic.ClassifyOperationFailure(
                    preflightFailures,
                    FfmpegOperationFailureKind.InputAccess),
                preflightFailures);
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

            var shouldDiagnoseInputs = false;
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
                            hardwareEncoder);
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
                            shouldDiagnoseInputs |= processResult.ProcessStarted && !processResult.TimedOut;
                            continue;
                        }

                        var validation = await _mediaValidator
                            .ValidateAsync(temporaryOutput, expectedDuration, cancellationToken)
                            .ConfigureAwait(false);
                        if (!validation.IsValid)
                        {
                            _logger.LogInformationMessage($"FFmpeg output rejected. reason={validation.FailureReason}");
                            operationFailureKind = FfmpegOperationFailureKind.OutputInvalid;
                            shouldDiagnoseInputs = true;
                            continue;
                        }

                        OutputArtifactPublicationEvidence? publicationEvidence = null;
                        if (outputArtifactOwnershipProvider is not null)
                        {
                            var capture = await outputArtifactOwnershipProvider
                                .CapturePublicationEvidenceAsync(temporaryOutput, cancellationToken)
                                .ConfigureAwait(false);
                            publicationEvidence = capture.Succeeded ? capture.Evidence : null;
                        }

                        File.Move(temporaryOutput, outputFile, overwrite: overwriteDestination);
                        if (publicationEvidence is not null
                            && !await VerifyPublishedIdentityAsync(
                                    outputArtifactOwnershipProvider,
                                    outputFile,
                                    publicationEvidence)
                                .ConfigureAwait(false))
                        {
                            publicationEvidence = null;
                        }

                        return new FfmpegOperationResult(
                            true,
                            outputFile,
                            null,
                            validation.Duration,
                            FfmpegOperationFailureKind.None,
                            [],
                            publicationEvidence);
                    }
                    finally
                    {
                        DeleteFile(temporaryOutput);
                    }
                }
            }

            var inputFailures = shouldDiagnoseInputs
                ? await FfmpegInputDiagnostic.FindInputFailuresAsync(
                        _processRunner,
                        _concurrencyGate,
                        orderedSegments.Select(segment => segment.FilePath),
                        ConcatTimeout,
                        cancellationToken)
                    .ConfigureAwait(false)
                : [];
            var failureKind = FfmpegInputDiagnostic.ClassifyOperationFailure(
                inputFailures,
                operationFailureKind);
            return FfmpegOperationResult.Failure(
                failureKind == FfmpegOperationFailureKind.InvalidInput
                    ? "One or more concat segments could not be decoded."
                    : "All FFmpeg concat strategies failed validation.",
                failureKind,
                inputFailures);
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

    private static async Task<bool> VerifyPublishedIdentityAsync(
        IOutputArtifactOwnershipProvider? outputArtifactOwnershipProvider,
        string outputFile,
        OutputArtifactPublicationEvidence publicationEvidence)
    {
        if (outputArtifactOwnershipProvider is null)
        {
            return false;
        }

        try
        {
            return await outputArtifactOwnershipProvider
                .VerifyPublishedObjectIdentityAsync(
                    outputFile,
                    publicationEvidence,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or ArgumentException
                                         or NotSupportedException)
        {
            return false;
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
