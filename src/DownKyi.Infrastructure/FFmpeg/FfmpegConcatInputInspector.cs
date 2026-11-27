using System.Text.Json;
using DownKyi.Application.Media;

namespace DownKyi.Infrastructure.FFmpeg;

internal enum FfmpegConcatInputStatus
{
    Compatible,
    RequiresEncoding,
    Failed
}

internal readonly record struct FfmpegConcatInputInspection(
    FfmpegConcatInputStatus Status,
    FfmpegOperationFailureKind FailureKind = FfmpegOperationFailureKind.None);

/// <summary>Checks only the streams the concat command will actually map.</summary>
internal sealed class FfmpegConcatInputInspector(IFfmpegProcessRunner processRunner)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] StreamFields =
    [
        "codec_name", "codec_type", "profile", "level", "width", "height", "pix_fmt",
        "sample_aspect_ratio", "field_order", "sample_rate", "channels", "channel_layout",
        "time_base", "extradata_hash"
    ];

    public async Task<FfmpegConcatInputInspection> InspectAsync(
        IReadOnlyList<FfmpegConcatSegment> segments,
        FfmpegEmbeddedAudioMode audioMode,
        CancellationToken cancellationToken)
    {
        // A container's estimated duration is not evidence for the DURL timeline.
        if (segments.Any(segment => segment.ExpectedDuration is not { } duration || duration <= TimeSpan.Zero))
        {
            return new(FfmpegConcatInputStatus.RequiresEncoding);
        }

        string[]? reference = null;
        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await processRunner.RunAsync(
                FfmpegCommandFactory.BuildConcatInputProbe(segment.FilePath),
                ProbeTimeout, cancellationToken).ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                // Metadata inspection is not fail-on-error decoding and cannot
                // establish that a completed source is corrupt.
                var failure = probe.ToOperationFailureKind();
                return new(FfmpegConcatInputStatus.Failed,
                    failure == FfmpegOperationFailureKind.InputInvalid
                        ? FfmpegOperationFailureKind.ProcessFailure : failure);
            }

            try
            {
                using var document = JsonDocument.Parse(probe.StandardOutput);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("streams", out var streams) ||
                    streams.ValueKind != JsonValueKind.Array ||
                    streams.EnumerateArray().Any(stream => stream.ValueKind != JsonValueKind.Object))
                {
                    return new(FfmpegConcatInputStatus.Failed, FfmpegOperationFailureKind.OutputInvalid);
                }

                var videos = streams.EnumerateArray()
                    .Where(stream => GetField(stream, "codec_type") == "video").ToArray();
                var audios = streams.EnumerateArray()
                    .Where(stream => GetField(stream, "codec_type") == "audio").ToArray();
                if (videos.Length == 0 || audioMode == FfmpegEmbeddedAudioMode.Required && audios.Length == 0)
                {
                    return new(FfmpegConcatInputStatus.Failed, FfmpegOperationFailureKind.OutputInvalid);
                }

                IEnumerable<JsonElement> selectedAudio = audioMode switch
                {
                    FfmpegEmbeddedAudioMode.Excluded => [],
                    FfmpegEmbeddedAudioMode.Required => audios.Take(1),
                    _ => audios
                };
                var selected = new[] { videos[0] }.Concat(selectedAudio).ToArray();
                // Keep the lossless path narrow: the Bilibili H.264/AAC contract
                // must include matching codec initialization data and time bases.
                if (GetField(videos[0], "codec_name") != "h264" ||
                    selected.Skip(1).Any(stream => GetField(stream, "codec_name") != "aac") ||
                    selected.Any(stream => string.IsNullOrEmpty(GetField(stream, "extradata_hash")) ||
                        string.IsNullOrEmpty(GetField(stream, "time_base"))))
                {
                    return new(FfmpegConcatInputStatus.RequiresEncoding);
                }

                var signature = selected.SelectMany(stream => StreamFields.Select(field => GetField(stream, field))).ToArray();
                if (reference != null && !reference.SequenceEqual(signature, StringComparer.Ordinal))
                {
                    return new(FfmpegConcatInputStatus.RequiresEncoding);
                }

                reference = signature;
            }
            catch (JsonException)
            {
                return new(FfmpegConcatInputStatus.Failed, FfmpegOperationFailureKind.OutputInvalid);
            }
        }

        return new(FfmpegConcatInputStatus.Compatible);
    }

    private static string GetField(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) ? value.ToString() : string.Empty;
}
