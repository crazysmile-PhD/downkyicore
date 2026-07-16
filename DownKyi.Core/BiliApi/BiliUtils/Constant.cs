using System.Collections.Immutable;

namespace DownKyi.Core.BiliApi.BiliUtils;

public static class Constant
{
    private static readonly ImmutableArray<QualityOption> Resolutions =
    [
        new("360P 流畅", 16),
        new("480P 清晰", 32),
        new("720P 高清", 64),
        new("720P 60帧", 74),
        new("1080P 高清", 80),
        new("1080P 高码率", 112),
        new("1080P 60帧", 116),
        new("4K 超清", 120),
        new("HDR 真彩", 125),
        new("杜比视界", 126),
        new("超高清 8K", 127)
    ];

    private static readonly ImmutableArray<QualityOption> CodecIds =
    [
        new("H.264/AVC", 7),
        new("H.265/HEVC", 12),
        new("AV1", 13)
    ];

    private static readonly ImmutableArray<QualityOption> AudioQualities =
    [
        new("低质量", 30216),
        new("中质量", 30232),
        new("高质量", 30280),
        new("Dolby Atmos", 30250),
        new("Hi-Res无损", 30251)
    ];

    public static IReadOnlyList<Quality> GetResolutions()
    {
        return CreateMutableOptions(Resolutions);
    }

    public static IReadOnlyList<Quality> GetCodecIds()
    {
        return CreateMutableOptions(CodecIds);
    }

    public static IReadOnlyList<Quality> GetAudioQualities()
    {
        return CreateMutableOptions(AudioQualities);
    }

    private static Quality[] CreateMutableOptions(ImmutableArray<QualityOption> options)
    {
        return options
            .Select(option => new Quality { Name = option.Name, Id = option.Id })
            .ToArray();
    }

    private readonly record struct QualityOption(string Name, int Id);
}
