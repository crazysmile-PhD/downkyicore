using DownKyi.Application.Settings;
using DownKyi.Infrastructure.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegHardwareEncoderDetectorTests
{
    [Fact]
    public async Task AutoSkipsUnavailableHardwareAndReusesTheWorkingEncoder()
    {
        var runner = new ProbeRunner(static (encoder, _) => Task.FromResult(
            new FfmpegProcessResult(encoder != "h264_nvenc", encoder == "h264_nvenc" ? 1 : 0,
                string.Empty, string.Empty, false)));
        var detector = CreateDetector(runner);

        var selected = await detector.SelectAsync(FfmpegHardwareAcceleration.Auto, TestContext.Current.CancellationToken);
        var repeated = await detector.SelectAsync(FfmpegHardwareAcceleration.Auto, TestContext.Current.CancellationToken);

        Assert.NotNull(selected);
        Assert.Equal(OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "h264_qsv", selected.EncoderName);
        Assert.Equal(selected, repeated);
        Assert.Equal(OperatingSystem.IsMacOS() ? 1 : 2, runner.Commands.Count);
        Assert.All(runner.Commands, command =>
        {
            Assert.DoesNotContain("-encoders", command.Arguments);
            Assert.Contains("-frames:v", command.Arguments);
            Assert.Equal("-", command.Arguments[^1]);
        });
    }

    [Fact]
    public async Task DisabledModeDoesNoProbeAndManualModeDoesNotTryOtherHardware()
    {
        var runner = new ProbeRunner(static (_, _) => Task.FromResult(Failure()));
        var detector = CreateDetector(runner);

        Assert.Null(await detector.SelectAsync(FfmpegHardwareAcceleration.Disabled, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Commands);
        Assert.Null(await detector.SelectAsync(FfmpegHardwareAcceleration.NvidiaNvenc, TestContext.Current.CancellationToken));
        Assert.Null(await detector.SelectAsync(FfmpegHardwareAcceleration.NvidiaNvenc, TestContext.Current.CancellationToken));
        Assert.Equal("h264_nvenc", GetEncoder(Assert.Single(runner.Commands)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableProcessOrTimeoutStopsProbingWithoutCachingTheFailure(bool timedOut)
    {
        var runner = new ProbeRunner((_, _) => Task.FromResult(
            new FfmpegProcessResult(false, -1, string.Empty, string.Empty, timedOut, ProcessStarted: timedOut)));
        var detector = CreateDetector(runner);

        Assert.Null(await detector.SelectAsync(FfmpegHardwareAcceleration.Auto, TestContext.Current.CancellationToken));
        Assert.Single(runner.Commands);
        Assert.Null(await detector.SelectAsync(FfmpegHardwareAcceleration.Auto, TestContext.Current.CancellationToken));
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task CanceledProbePreservesCancellationAndCanBeRetried()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var runner = new ProbeRunner(async (_, token) =>
        {
            if (++attempts == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            }

            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
        });
        var detector = CreateDetector(runner);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = detector.SelectAsync(FfmpegHardwareAcceleration.NvidiaNvenc, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.NotNull(await detector.SelectAsync(FfmpegHardwareAcceleration.NvidiaNvenc, TestContext.Current.CancellationToken));
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task VaapiProbeAndMediaWorkShareTheHardwareFrameArguments()
    {
        var runner = new ProbeRunner(static (_, _) => Task.FromResult(new FfmpegProcessResult(true, 0, "", "", false)));
        var encoder = await CreateDetector(runner).SelectAsync(FfmpegHardwareAcceleration.Vaapi, TestContext.Current.CancellationToken);
        Assert.NotNull(encoder);
        var probe = Assert.Single(runner.Commands);
        var media = FfmpegCommandFactory.BuildConcat("segments.txt", "output.mp4", FfmpegConcatStrategy.HardwareEncoder,
            encoder, DownKyi.Application.Media.FfmpegEmbeddedAudioMode.Excluded);

        Assert.All(new[] { probe, media }, command =>
        {
            Assert.Contains("format=nv12,hwupload", command.Arguments);
            Assert.DoesNotContain("yuv420p", command.Arguments);
        });
    }

    [Fact]
    [Trait("Category", "FfmpegIntegration")]
    public async Task OneFrameProbeRunsWithoutCreatingAnOutputFile()
    {
        var encoder = new FfmpegHardwareEncoderProfile(FfmpegHardwareAcceleration.Disabled, "Test CPU", "libx264",
            ["-c:v", "libx264", "-pix_fmt:v:0", "yuv420p"]);
        var result = await new FfmpegProcessRunner().RunAsync(
            FfmpegCommandFactory.BuildHardwareEncoderProbe(encoder), TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        if (!result.ProcessStarted) Assert.Skip("ffmpeg is required for the encoder probe integration test.");

        Assert.True(result.Succeeded, result.StandardError);
    }

    private static FfmpegHardwareEncoderDetector CreateDetector(ProbeRunner runner) =>
        new(NullLogger<FfmpegHardwareEncoderDetector>.Instance, runner);

    private static FfmpegProcessResult Failure() => new(false, 1, string.Empty, "No compatible device", false);

    private static string GetEncoder(FfmpegCommand command) => command.Arguments[command.Arguments.ToList().IndexOf("-c:v") + 1];

    private sealed class ProbeRunner(Func<string, CancellationToken, Task<FfmpegProcessResult>> run) : IFfmpegProcessRunner
    {
        public List<FfmpegCommand> Commands { get; } = [];

        public Task<FfmpegProcessResult> RunAsync(FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return run(GetEncoder(command), cancellationToken);
        }
    }
}
