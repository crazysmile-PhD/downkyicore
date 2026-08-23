using DownKyi.Application.Diagnostics;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DownKyi.Core.FFmpeg;

public interface IFfmpegMediaMuxer
{
    Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        VideoApplicationSettings videoSettings,
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputVideo,
        bool overwriteDestination,
        Action<string>? action = null,
        CancellationToken cancellationToken = default);

    Task<FfmpegOperationResult> MergeMediaAsync(
        VideoApplicationSettings videoSettings,
        string? audio,
        string? video,
        string destination,
        bool overwriteDestination,
        CancellationToken cancellationToken = default);
}

public sealed class FfmpegProcessor : IFfmpegMediaMuxer
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromHours(2);
    private readonly AsyncConcurrencyGate _operationGate;
    private readonly FfmpegConcatRuntime _concatRuntime;
    private readonly IFfmpegProcessRunner _processRunner;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<FfmpegProcessor> _logger;
    private readonly FfmpegHardwareEncoderDetector _hardwareEncoderDetector;

    public FfmpegProcessor(ISettingsStore settingsStore, ILoggerFactory loggerFactory)
        : this(settingsStore, loggerFactory, new FfmpegProcessRunner())
    {
    }

    internal FfmpegProcessor(
        ISettingsStore settingsStore,
        ILoggerFactory loggerFactory,
        IFfmpegProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _settingsStore = settingsStore;
        _logger = loggerFactory.CreateLogger<FfmpegProcessor>();
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _hardwareEncoderDetector = new FfmpegHardwareEncoderDetector(
            loggerFactory.CreateLogger<FfmpegHardwareEncoderDetector>());
        _operationGate = new AsyncConcurrencyGate(
            () => _settingsStore.Current.Video.FfmpegMaxParallelJobs);
        _concatRuntime = new FfmpegConcatRuntime(
            _processRunner,
            new FfmpegMediaValidator(_processRunner),
            _operationGate,
            loggerFactory.CreateLogger<FfmpegConcatRuntime>());
    }

    public async Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        VideoApplicationSettings videoSettings,
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputVideo,
        bool overwriteDestination,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoSettings);
        var encoder = await _hardwareEncoderDetector.SelectAsync(
                videoSettings.FfmpegHardwareAcceleration,
                cancellationToken)
            .ConfigureAwait(false);
        return await _concatRuntime.ConcatAsync(
                segments,
                outputVideo,
                encoder,
                allowStreamCopy: false,
                overwriteDestination,
                action,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> MergeVideoAsync(
        VideoApplicationSettings videoSettings,
        string? audio,
        string? video,
        string destination,
        bool overwriteDestination,
        CancellationToken cancellationToken = default)
    {
        var result = await MergeMediaAsync(
            videoSettings,
            audio,
            video,
            destination,
            overwriteDestination,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task<FfmpegOperationResult> MergeMediaAsync(
        VideoApplicationSettings videoSettings,
        string? audio,
        string? video,
        string destination,
        bool overwriteDestination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoSettings);
        var requestedInputs = new[] { audio, video }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
        var audioPath = !string.IsNullOrWhiteSpace(audio) ? audio : null;
        var videoPath = !string.IsNullOrWhiteSpace(video) ? video : null;
        if (audioPath == null && videoPath == null)
        {
            return FfmpegOperationResult.Failure(
                "No media inputs were provided.",
                FfmpegOperationFailureKind.NoInput);
        }

        var preflightFailures = FfmpegInputDiagnostic.ProbeInputs(requestedInputs);
        if (preflightFailures.Count > 0)
        {
            return FfmpegOperationResult.Failure(
                preflightFailures.Any(failure => failure.CanInvalidate)
                    ? "One or more media inputs are missing or empty."
                    : "One or more media inputs could not be accessed.",
                FfmpegInputDiagnostic.ClassifyOperationFailure(
                    preflightFailures,
                    FfmpegOperationFailureKind.InputAccess),
                preflightFailures);
        }

        var outputResult = await RunToFileAsync(
            temporaryOutput => FfmpegCommandFactory.BuildMerge(
                audioPath,
                videoPath,
                temporaryOutput,
                videoSettings.IsTranscodingAacToMp3 == AllowStatus.Yes),
            destination,
            overwriteDestination,
            action: null,
            cancellationToken).ConfigureAwait(false);
        if (outputResult.Succeeded)
        {
            return new FfmpegOperationResult(
                true,
                destination,
                null,
                TimeSpan.Zero,
                FfmpegOperationFailureKind.None,
                []);
        }

        if (outputResult.FailureKind != FfmpegOperationFailureKind.ProcessFailure)
        {
            return FfmpegOperationResult.Failure(
                "FFmpeg output could not be finalized.",
                outputResult.FailureKind);
        }

        var inputFailures = await FfmpegInputDiagnostic.FindInputFailuresAsync(
                _processRunner,
                _operationGate,
                requestedInputs,
                OperationTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var failureKind = FfmpegInputDiagnostic.ClassifyOperationFailure(
            inputFailures,
            outputResult.FailureKind);
        return FfmpegOperationResult.Failure(
            failureKind == FfmpegOperationFailureKind.InvalidInput
                ? "One or more media inputs could not be decoded."
                : "FFmpeg output could not be finalized.",
            failureKind,
            inputFailures);
    }

    public Task<bool> DelogoAsync(
        string video,
        string destination,
        int x,
        int y,
        int width,
        int height,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        return RunToFileSucceededAsync(
            temporaryOutput => FfmpegCommandFactory.BuildDelogo(
                video,
                temporaryOutput,
                x,
                y,
                width,
                height),
            destination,
            overwriteDestination: true,
            action,
            cancellationToken);
    }

    public Task<bool> ExtractAudioAsync(
        string video,
        string audio,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        return RunToFileSucceededAsync(
            temporaryOutput => FfmpegCommandFactory.BuildExtractAudio(video, temporaryOutput),
            audio,
            overwriteDestination: true,
            action,
            cancellationToken);
    }

    public Task<bool> ExtractVideoAsync(
        string video,
        string destination,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        return RunToFileSucceededAsync(
            temporaryOutput => FfmpegCommandFactory.BuildExtractVideo(video, temporaryOutput),
            destination,
            overwriteDestination: true,
            action,
            cancellationToken);
    }

    public async Task<MemoryStream> ExtractVideoFrameAsync(
        string inputPath,
        TimeSpan timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var temporaryFile = Path.Combine(Path.GetTempPath(), $"downkyi-frame-{Guid.NewGuid():N}.jpg");
        try
        {
            using var slot = await _operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var result = await _processRunner.RunAsync(
                FfmpegCommandFactory.BuildExtractFrame(inputPath, temporaryFile, timestamp),
                OperationTimeout,
                cancellationToken).ConfigureAwait(false);
            LogResult(result, "extract-frame", action: null);
            if (!result.Succeeded || !File.Exists(temporaryFile))
            {
                throw new InvalidOperationException("FFmpeg could not extract the requested frame.");
            }

            var bytes = await File.ReadAllBytesAsync(temporaryFile, cancellationToken).ConfigureAwait(false);
            return new MemoryStream(bytes, writable: false);
        }
        finally
        {
            DeleteFile(temporaryFile);
        }
    }

    private async Task<bool> RunToFileSucceededAsync(
        Func<string, FfmpegCommand> commandFactory,
        string destination,
        bool overwriteDestination,
        Action<string>? action,
        CancellationToken cancellationToken)
    {
        var result = await RunToFileAsync(
            commandFactory,
            destination,
            overwriteDestination,
            action,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    private async Task<FfmpegOutputResult> RunToFileAsync(
        Func<string, FfmpegCommand> commandFactory,
        string destination,
        bool overwriteDestination,
        Action<string>? action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var extension = Path.GetExtension(destination);
        var temporaryOutput = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(destination))!,
            $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.partial{extension}");
        try
        {
            using var slot = await _operationGate.EnterAsync(cancellationToken).ConfigureAwait(false);
            var command = commandFactory(temporaryOutput);
            var result = await _processRunner
                .RunAsync(command, OperationTimeout, cancellationToken)
                .ConfigureAwait(false);
            LogResult(result, command.Operation, action);
            if (!result.Succeeded)
            {
                return FfmpegOutputResult.Failure(
                    !result.ProcessStarted
                        ? FfmpegOperationFailureKind.ProcessUnavailable
                        : result.TimedOut
                            ? FfmpegOperationFailureKind.Timeout
                            : FfmpegOperationFailureKind.ProcessFailure);
            }

            if (!File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
            {
                return FfmpegOutputResult.Failure(FfmpegOperationFailureKind.OutputInvalid);
            }

            File.Move(temporaryOutput, destination, overwrite: overwriteDestination);
            return FfmpegOutputResult.Success();
        }
        catch (IOException e)
        {
            _logger.LogErrorMessage("FFmpeg output finalization failed.", e);
            return FfmpegOutputResult.Failure(
                !overwriteDestination && File.Exists(destination)
                    ? FfmpegOperationFailureKind.DestinationConflict
                    : FfmpegOperationFailureKind.OutputIoFailure);
        }
        catch (UnauthorizedAccessException e)
        {
            _logger.LogErrorMessage("FFmpeg output finalization was denied.", e);
            return FfmpegOutputResult.Failure(
                FfmpegOperationFailureKind.DestinationAccessDenied);
        }
        finally
        {
            DeleteFile(temporaryOutput);
        }
    }

    private void LogResult(FfmpegProcessResult result, string operation, Action<string>? action)
    {
        var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"FFmpeg {operation}: exit={result.ExitCode}; timedOut={result.TimedOut}"
            : result.StandardError;
        action?.Invoke(diagnostic);
        if (result.Succeeded)
        {
            _logger.LogInformationMessage($"FFmpeg operation completed. operation={operation}; exit={result.ExitCode}");
        }
        else
        {
            _logger.LogErrorMessage(
                $"FFmpeg operation failed. operation={operation}; exit={result.ExitCode}; timedOut={result.TimedOut}");
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

    private sealed record FfmpegOutputResult(
        bool Succeeded,
        FfmpegOperationFailureKind FailureKind)
    {
        public static FfmpegOutputResult Success() =>
            new(true, FfmpegOperationFailureKind.None);

        public static FfmpegOutputResult Failure(FfmpegOperationFailureKind failureKind) =>
            new(false, failureKind);
    }
}
