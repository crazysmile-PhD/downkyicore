using DownKyi.Core.Logging;
using DownKyi.Core.Settings;

namespace DownKyi.Core.FFMpeg;

public sealed class FfmpegProcessor
{
    private const string Tag = nameof(FfmpegProcessor);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromHours(2);
    private readonly AsyncConcurrencyGate _operationGate;
    private readonly FfmpegConcatRuntime _concatRuntime;
    private readonly FfmpegProcessRunner _processRunner;
    private readonly SettingsManager _settings;

    public FfmpegProcessor(ISettingsStore settingsStore)
    {
        _settings = settingsStore?.Settings ?? throw new ArgumentNullException(nameof(settingsStore));
        _processRunner = new FfmpegProcessRunner();
        _operationGate = new AsyncConcurrencyGate(_settings.GetFfmpegMaxParallelJobs);
        _concatRuntime = new FfmpegConcatRuntime(
            _processRunner,
            new FfmpegMediaValidator(_processRunner),
            _settings.GetFfmpegMaxParallelJobs);
    }

    public async Task<FfmpegOperationResult> ConcatDurlVideosAsync(
        IReadOnlyList<FfmpegConcatSegment> segments,
        string outputVideo,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        var encoder = await FfmpegHardwareEncoderDetector.SelectAsync(
                _settings.GetFfmpegHardwareAcceleration(),
                cancellationToken)
            .ConfigureAwait(false);
        return await _concatRuntime.ConcatAsync(
                segments,
                outputVideo,
                encoder,
                allowStreamCopy: false,
                action,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> MergeVideoAsync(
        string? audio,
        string? video,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var audioPath = !string.IsNullOrWhiteSpace(audio) && File.Exists(audio) ? audio : null;
        var videoPath = !string.IsNullOrWhiteSpace(video) && File.Exists(video) ? video : null;
        if (audioPath == null && videoPath == null)
        {
            return false;
        }

        var succeeded = await RunToFileAsync(
            temporaryOutput => FfmpegCommandFactory.BuildMerge(
                audioPath,
                videoPath,
                temporaryOutput,
                _settings.GetIsTranscodingAacToMp3() == AllowStatus.Yes),
            destination,
            action: null,
            cancellationToken).ConfigureAwait(false);
        if (succeeded)
        {
            DeleteInput(audio);
            DeleteInput(video);
        }

        return succeeded;
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
        return RunToFileAsync(
            temporaryOutput => FfmpegCommandFactory.BuildDelogo(
                video,
                temporaryOutput,
                x,
                y,
                width,
                height),
            destination,
            action,
            cancellationToken);
    }

    public Task<bool> ExtractAudioAsync(
        string video,
        string audio,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        return RunToFileAsync(
            temporaryOutput => FfmpegCommandFactory.BuildExtractAudio(video, temporaryOutput),
            audio,
            action,
            cancellationToken);
    }

    public Task<bool> ExtractVideoAsync(
        string video,
        string destination,
        Action<string>? action = null,
        CancellationToken cancellationToken = default)
    {
        return RunToFileAsync(
            temporaryOutput => FfmpegCommandFactory.BuildExtractVideo(video, temporaryOutput),
            destination,
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

    private async Task<bool> RunToFileAsync(
        Func<string, FfmpegCommand> commandFactory,
        string destination,
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
            if (!result.Succeeded || !File.Exists(temporaryOutput) || new FileInfo(temporaryOutput).Length == 0)
            {
                return false;
            }

            File.Move(temporaryOutput, destination, overwrite: true);
            return true;
        }
        catch (IOException e)
        {
            LogManager.Error(Tag, e);
            return false;
        }
        catch (UnauthorizedAccessException e)
        {
            LogManager.Error(Tag, e);
            return false;
        }
        finally
        {
            DeleteFile(temporaryOutput);
        }
    }

    private static void LogResult(FfmpegProcessResult result, string operation, Action<string>? action)
    {
        var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"FFmpeg {operation}: exit={result.ExitCode}; timedOut={result.TimedOut}"
            : result.StandardError;
        action?.Invoke(diagnostic);
        if (result.Succeeded)
        {
            LogManager.Info(Tag, $"FFmpeg operation completed. operation={operation}; exit={result.ExitCode}");
        }
        else
        {
            LogManager.Error(Tag, new InvalidOperationException(
                $"FFmpeg operation failed. operation={operation}; exit={result.ExitCode}; timedOut={result.TimedOut}"));
        }
    }

    private static void DeleteInput(string? file)
    {
        if (!string.IsNullOrWhiteSpace(file))
        {
            DeleteFile(file);
        }
    }

    private static void DeleteFile(string file)
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
            LogManager.Debug(Tag, $"FFmpeg cleanup failed: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            LogManager.Debug(Tag, $"FFmpeg cleanup was denied: {e.Message}");
        }
    }
}
