using DownKyi.Application.Diagnostics;
using DownKyi.Application.Media;
using DownKyi.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DownKyi.Infrastructure.FFmpeg;

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
            _operationGate,
            loggerFactory.CreateLogger<FfmpegConcatRuntime>());
    }

    public async Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        VideoApplicationSettings videoSettings,
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputVideo,
        bool overwriteDestination,
        Action<string>? action = null,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? cover = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoSettings);
        return await RunWithPreparedCoverAsync(
                NormalizeOptionalInput(cover),
                outputVideo,
                async normalizedCover =>
                {
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
                            embeddedAudioMode,
                            normalizedCover,
                            cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<FfmpegOperationResult> MergeMediaAsync(
        VideoApplicationSettings videoSettings,
        string? audio,
        string? video,
        string destination,
        bool overwriteDestination,
        FfmpegEmbeddedAudioMode embeddedAudioMode = FfmpegEmbeddedAudioMode.Optional,
        string? cover = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoSettings);
        var audioPath = !string.IsNullOrWhiteSpace(audio) ? audio : null;
        var videoPath = !string.IsNullOrWhiteSpace(video) ? video : null;
        if (audioPath == null && videoPath == null)
        {
            return FfmpegOperationResult.Failure(
                "No media inputs were provided.",
                FfmpegOperationFailureKind.NoInput);
        }

        return await RunWithPreparedCoverAsync(
                videoPath == null ? null : NormalizeOptionalInput(cover),
                destination,
                async normalizedCover =>
                {
                    var outputResult = await RunToFileAsync(
                        temporaryOutput => FfmpegCommandFactory.BuildMerge(
                            audioPath,
                            videoPath,
                            temporaryOutput,
                            videoSettings.IsTranscodingAacToMp3 == AllowStatus.Yes,
                            embeddedAudioMode,
                            normalizedCover),
                        destination,
                        overwriteDestination,
                        action: null,
                        cancellationToken).ConfigureAwait(false);
                    return outputResult.Succeeded
                        ? new FfmpegOperationResult(
                            true,
                            destination,
                            null,
                            FfmpegOperationFailureKind.None)
                        : FfmpegOperationResult.Failure(
                            "FFmpeg output could not be finalized.",
                            outputResult.FailureKind);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? NormalizeOptionalInput(string? path) =>
        !string.IsNullOrWhiteSpace(path) ? path : null;

    private async Task<FfmpegOperationResult> RunWithPreparedCoverAsync(
        string? cover,
        string destination,
        Func<string?, Task<FfmpegOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        string? normalizedCover = null;
        try
        {
            if (cover != null)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(destination))
                    ?? throw new InvalidOperationException("The output directory is unavailable.");
                normalizedCover = Path.Combine(
                    directory,
                    $".downkyi-cover-{Guid.NewGuid():N}.jpg");
                var coverResult = await RunToFileAsync(
                    temporaryOutput => FfmpegCommandFactory.BuildNormalizeCover(
                        cover,
                        temporaryOutput),
                    normalizedCover,
                    overwriteDestination: false,
                    action: null,
                    cancellationToken).ConfigureAwait(false);
                if (!coverResult.Succeeded)
                {
                    return FfmpegOperationResult.Failure(
                        "The cover image could not be converted to JPEG.",
                        coverResult.FailureKind,
                        FfmpegOperationFailureStage.Cover);
                }
            }

            return await operation(normalizedCover).ConfigureAwait(false);
        }
        finally
        {
            DeleteFile(normalizedCover);
        }
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

    private void DeleteFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

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
