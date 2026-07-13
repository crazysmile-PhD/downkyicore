using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DownKyi.Core.FFMpeg;

internal sealed record FfmpegMediaValidationResult(
    bool IsValid,
    TimeSpan Duration,
    string? FailureReason)
{
    public static FfmpegMediaValidationResult Failure(string reason)
    {
        return new FfmpegMediaValidationResult(false, TimeSpan.Zero, reason);
    }
}

internal interface IFfmpegMediaValidator
{
    Task<FfmpegMediaValidationResult> ValidateAsync(
        string mediaFile,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken = default);
}

internal sealed class FfmpegMediaValidator : IFfmpegMediaValidator
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
    private readonly IFfmpegProcessRunner _processRunner;

    public FfmpegMediaValidator(IFfmpegProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<FfmpegMediaValidationResult> ValidateAsync(
        string mediaFile,
        TimeSpan expectedDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFile);
        if (!File.Exists(mediaFile) || new FileInfo(mediaFile).Length == 0)
        {
            return FfmpegMediaValidationResult.Failure("Output file is missing or empty.");
        }

        var probe = await _processRunner
            .RunAsync(FfmpegCommandFactory.BuildProbe(mediaFile), ProcessTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return FfmpegMediaValidationResult.Failure("ffprobe could not read the output.");
        }

        FfprobeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(probe.StandardOutput, FfprobeJsonContext.Default.FfprobeDocument);
        }
        catch (JsonException)
        {
            return FfmpegMediaValidationResult.Failure("ffprobe returned malformed JSON.");
        }

        if (document?.Streams?.Any(stream => string.Equals(
                stream.CodecType,
                "video",
                StringComparison.OrdinalIgnoreCase)) != true)
        {
            return FfmpegMediaValidationResult.Failure("Output has no video stream.");
        }

        if (!double.TryParse(
                document.Format?.Duration,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var durationSeconds) ||
            !double.IsFinite(durationSeconds) ||
            durationSeconds <= 0)
        {
            return FfmpegMediaValidationResult.Failure("Output duration is missing or invalid.");
        }

        var duration = TimeSpan.FromSeconds(durationSeconds);
        if (!IsDurationClose(duration, expectedDuration))
        {
            return FfmpegMediaValidationResult.Failure("Output duration does not match the source segments.");
        }

        foreach (var position in GetSeekPositions(duration))
        {
            var decode = await _processRunner
                .RunAsync(FfmpegCommandFactory.BuildSeekDecode(mediaFile, position), ProcessTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!decode.Succeeded || !DecodedAtLeastOneFrame(decode.StandardOutput))
            {
                return FfmpegMediaValidationResult.Failure("Output cannot decode after seeking.");
            }
        }

        return new FfmpegMediaValidationResult(true, duration, null);
    }

    internal static bool IsDurationClose(TimeSpan actual, TimeSpan expected)
    {
        if (expected <= TimeSpan.Zero)
        {
            return true;
        }

        var tolerance = TimeSpan.FromSeconds(Math.Max(3, expected.TotalSeconds * 0.05));
        return (actual - expected).Duration() <= tolerance;
    }

    internal static IReadOnlyList<TimeSpan> GetSeekPositions(TimeSpan duration)
    {
        var middle = TimeSpan.FromTicks(duration.Ticks / 2);
        var tailOffset = TimeSpan.FromSeconds(Math.Min(2, duration.TotalSeconds * 0.1));
        var tail = duration - tailOffset;
        return Math.Abs((tail - middle).TotalMilliseconds) < 100
            ? new[] { middle }
            : new[] { middle, tail };
    }

    private static bool DecodedAtLeastOneFrame(string progressOutput)
    {
        foreach (var line in progressOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("frame=", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan("frame=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) &&
                frames > 0)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class FfprobeDocument
{
    [JsonPropertyName("streams")]
    public IReadOnlyList<FfprobeStream>? Streams { get; init; }

    [JsonPropertyName("format")]
    public FfprobeFormat? Format { get; init; }
}

internal sealed class FfprobeStream
{
    [JsonPropertyName("codec_type")]
    public string? CodecType { get; init; }
}

internal sealed class FfprobeFormat
{
    [JsonPropertyName("duration")]
    public string? Duration { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(FfprobeDocument))]
internal sealed partial class FfprobeJsonContext : JsonSerializerContext;
