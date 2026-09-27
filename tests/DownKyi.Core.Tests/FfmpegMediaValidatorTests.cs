using System.Globalization;
using DownKyi.Core.FFmpeg;

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
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(19)],
            runner.SeekPositions);
    }

    [Fact]
    public async Task ValidateRejectsTruncatedOutputAtExpectedTailCheckpoint()
    {
        var runner = new ProbeProcessRunner("""
            {"streams":[{"codec_type":"video"}],"format":{"duration":"960.0"}}
            """, decodableThrough: TimeSpan.FromSeconds(960));
        var validator = new FfmpegMediaValidator(runner);

        var result = await validator.ValidateAsync(
            _mediaFile,
            TimeSpan.FromSeconds(1000),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Equal(
            [TimeSpan.Zero, TimeSpan.FromSeconds(500), TimeSpan.FromSeconds(999)],
            runner.SeekPositions);
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
        Assert.Empty(runner.SeekPositions);
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
        Assert.Single(runner.SeekPositions);
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
        private readonly TimeSpan? _decodableThrough;

        public ProbeProcessRunner(
            string probeJson,
            int decodedFrames = 1,
            TimeSpan? decodableThrough = null)
        {
            _probeJson = probeJson;
            _decodedFrames = decodedFrames;
            _decodableThrough = decodableThrough;
        }

        public List<TimeSpan> SeekPositions { get; } = [];

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

            var seekIndex = command.Arguments.ToList().IndexOf("-ss");
            Assert.True(seekIndex >= 0);
            var seekPosition = TimeSpan.FromSeconds(double.Parse(
                command.Arguments[seekIndex + 1],
                CultureInfo.InvariantCulture));
            SeekPositions.Add(seekPosition);
            var decodedFrames = _decodableThrough is null || seekPosition <= _decodableThrough
                ? _decodedFrames
                : 0;
            return Task.FromResult(new FfmpegProcessResult(
                true,
                0,
                $"frame={decodedFrames}",
                string.Empty,
                false));
        }
    }
}
