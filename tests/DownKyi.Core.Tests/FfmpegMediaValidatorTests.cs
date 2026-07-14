using DownKyi.Core.FFMpeg;

namespace DownKyi.Core.Tests;

public sealed class FfmpegMediaValidatorTests : IDisposable
{
    private readonly string _mediaFile = Path.Combine(Path.GetTempPath(), $"downkyi-probe-{Guid.NewGuid():N}.mp4");

    public FfmpegMediaValidatorTests()
    {
        File.WriteAllBytes(_mediaFile, [1, 2, 3]);
    }

    [Fact]
    public async Task ValidateAcceptsVideoWithMatchingDurationAndDecodableSeeks()
    {
        var runner = new ProbeProcessRunner("""
            {"streams":[{"codec_type":"video"}],"format":{"duration":"20.0"}}
            """);
        var validator = new FfmpegMediaValidator(runner);

        var result = await validator.ValidateAsync(
            _mediaFile,
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsValid);
        Assert.Equal(2, runner.SeekDecodeCount);
    }

    [Fact]
    public async Task ValidateRejectsDurationMismatchBeforeSeekDecode()
    {
        var runner = new ProbeProcessRunner("""
            {"streams":[{"codec_type":"video"}],"format":{"duration":"8.0"}}
            """);
        var validator = new FfmpegMediaValidator(runner);

        var result = await validator.ValidateAsync(
            _mediaFile,
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(0, runner.SeekDecodeCount);
    }

    [Fact]
    public async Task ValidateRejectsOutputWithoutVideoStream()
    {
        var runner = new ProbeProcessRunner("""
            {"streams":[{"codec_type":"audio"}],"format":{"duration":"20.0"}}
            """);
        var validator = new FfmpegMediaValidator(runner);

        var result = await validator.ValidateAsync(
            _mediaFile,
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(0, runner.SeekDecodeCount);
    }

    [Fact]
    public async Task ValidateRejectsSeekThatDecodesNoFrames()
    {
        var runner = new ProbeProcessRunner("""
            {"streams":[{"codec_type":"video"}],"format":{"duration":"20.0"}}
            """, decodedFrames: 0);
        var validator = new FfmpegMediaValidator(runner);

        var result = await validator.ValidateAsync(
            _mediaFile,
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(1, runner.SeekDecodeCount);
    }

    public void Dispose()
    {
        File.Delete(_mediaFile);
        GC.SuppressFinalize(this);
    }

    private sealed class ProbeProcessRunner : IFfmpegProcessRunner
    {
        private readonly string _probeJson;
        private readonly int _decodedFrames;

        public ProbeProcessRunner(string probeJson, int decodedFrames = 1)
        {
            _probeJson = probeJson;
            _decodedFrames = decodedFrames;
        }

        public int SeekDecodeCount { get; private set; }

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Operation == "probe-media")
            {
                return Task.FromResult(new FfmpegProcessResult(true, 0, _probeJson, string.Empty, false));
            }

            SeekDecodeCount++;
            return Task.FromResult(new FfmpegProcessResult(
                true,
                0,
                $"frame={_decodedFrames}",
                string.Empty,
                false));
        }
    }
}
