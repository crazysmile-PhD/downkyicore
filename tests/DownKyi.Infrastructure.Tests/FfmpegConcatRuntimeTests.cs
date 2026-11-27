using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegConcatRuntimeTests : IDisposable
{
    private static readonly string[] HardwareArguments = ["-c:v", "h264_nvenc"];
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-concat-{Guid.NewGuid():N}");

    public FfmpegConcatRuntimeTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task ConcatFinalizesNonEmptyOutputWithoutDeletingRetryInputs()
    {
        var first = CreateSegment("first.flv");
        var second = CreateSegment("second.flv");
        var runner = new RecordingConcatRunner();
        var runtime = new FfmpegConcatRuntime(
            runner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second),
                new FfmpegConcatSegment(1, first)
            ],
            output,
            resolveHardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.NotEqual(0, new FileInfo(output).Length);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.DoesNotContain("copy", runner.Commands[0].Arguments);
        Assert.True(runner.ConcatListLines[0].Contains("first.flv", StringComparison.Ordinal));
        Assert.True(runner.ConcatListLines[1].Contains("second.flv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcatRejectsEmptyOutputAfterSuccessfulProcess()
    {
        var segment = CreateSegment("segment.flv");
        var runner = new RecordingConcatRunner(writeNonEmptyOutput: false);
        var runtime = new FfmpegConcatRuntime(
            runner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment)],
            output,
            resolveHardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.OutputInvalid, result.FailureKind);
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(segment));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.partial.mp4"));
    }

    [Fact]
    public async Task ConcatDoesNotOverwriteExistingDestinationWhenOwnershipIsDenied()
    {
        var segment = CreateSegment("segment.flv");
        var runner = new RecordingConcatRunner();
        var runtime = new FfmpegConcatRuntime(
            runner,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "owned-by-another-task.mp4");
        byte[] existingContent = [9, 8, 7];
        await File.WriteAllBytesAsync(output, existingContent, TestContext.Current.CancellationToken);

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment)],
            output,
            resolveHardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.DestinationConflict, result.FailureKind);
        Assert.Equal(existingContent, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(segment));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.partial.mp4"));
    }

    [Fact]
    public async Task ConcatProcessFailureDoesNotRunAdditionalDecodeOrDeleteInputs()
    {
        var first = CreateSegment("diagnostic-first.flv");
        var corrupt = CreateSegment("diagnostic-corrupt.flv");
        var third = CreateSegment("diagnostic-third.flv");
        var runner = new FailedConcatRunner();
        var logger = new RecordingLogger<FfmpegConcatRuntime>();
        var runtime = new FfmpegConcatRuntime(
            runner,
            new AsyncConcurrencyGate(() => 1),
            logger);

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(1, first),
                new FfmpegConcatSegment(2, corrupt),
                new FfmpegConcatSegment(3, third)
            ],
            Path.Combine(_testDirectory, "diagnostic-output.mp4"),
            resolveHardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.ProcessFailure, result.FailureKind);
        Assert.Equal(1, runner.CallCount);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(corrupt));
        Assert.True(File.Exists(third));
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error
                     && entry.Message ==
                     "FFmpeg operation failed. operation=concat-CpuEncoder; exit=1; timedOut=False");
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateSegment(string name)
    {
        var path = Path.Combine(_testDirectory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task CompatibleInputsCopyOnceWithDeclaredDurationsWithoutResolvingHardware()
    {
        var first = CreateSegment("copy-first.flv");
        var second = CreateSegment("copy-second.flv");
        var runner = new InspectedConcatRunner();
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "copied.mp4");

        var result = await runtime.ConcatAsync(
            [new(2, second, TimeSpan.FromSeconds(3)), new(1, first, TimeSpan.FromSeconds(2.5))],
            output,
            _ => throw new InvalidOperationException("A compatible copy must not probe hardware."),
            allowStreamCopy: true,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(["probe-concat-input", "probe-concat-input", "concat-StreamCopy"], runner.Operations);
        Assert.Equal("duration 2.5", runner.ListLines[1]);
        Assert.Equal("duration 3", runner.ListLines[3]);
        Assert.Contains("copy", runner.MediaCommand!.Arguments);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedInspectionDoesNotTryEncodingOrPublishOutput(bool malformed)
    {
        var segment = CreateSegment("bad-inspection.flv");
        var runner = new InspectedConcatRunner(malformed: malformed, failProbe: !malformed);
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "inspection-must-not-publish.mp4");

        var result = await runtime.ConcatAsync([new(1, segment, TimeSpan.FromSeconds(2))], output,
            _ => throw new InvalidOperationException("A failed inspection cannot choose another encoder."),
            allowStreamCopy: true, overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(malformed ? FfmpegOperationFailureKind.OutputInvalid : FfmpegOperationFailureKind.ProcessFailure,
            result.FailureKind);
        Assert.Equal(["probe-concat-input"], runner.Operations);
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(segment));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, ".downkyi-concat-*"));
    }

    [Fact]
    public async Task DifferentCodecConfigurationChoosesEncodingBeforeMediaWork()
    {
        var first = CreateSegment("config-first.flv");
        var second = CreateSegment("config-second.flv");
        var runner = new InspectedConcatRunner(differentConfiguration: true);
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "encoded.mp4");
        var choices = 0;

        var result = await runtime.ConcatAsync(
            [new(1, first, TimeSpan.FromSeconds(2)), new(2, second, TimeSpan.FromSeconds(2))], output,
            _ => { choices++; return Task.FromResult<FfmpegHardwareEncoderProfile?>(null); },
            allowStreamCopy: true, overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, choices);
        Assert.Equal(["probe-concat-input", "probe-concat-input", "concat-CpuEncoder"], runner.Operations);
    }

    [Theory]
    [InlineData(FfmpegEmbeddedAudioMode.Excluded, true)]
    [InlineData(FfmpegEmbeddedAudioMode.Required, false)]
    public async Task CompatibilityChecksOnlyTheRequestedStreams(FfmpegEmbeddedAudioMode audioMode, bool copy)
    {
        var first = CreateSegment("audio-first.flv");
        var second = CreateSegment("audio-second.flv");
        var runner = new InspectedConcatRunner(differentAudio: true);
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var result = await runtime.ConcatAsync(
            [new(1, first, TimeSpan.FromSeconds(2)), new(2, second, TimeSpan.FromSeconds(2))],
            Path.Combine(_testDirectory, "selected-streams.mp4"),
            _ => copy ? throw new InvalidOperationException("Unrequested audio cannot force encoding.")
                : Task.FromResult<FfmpegHardwareEncoderProfile?>(null),
            allowStreamCopy: true, overwriteDestination: false, embeddedAudioMode: audioMode,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(copy ? "concat-StreamCopy" : "concat-CpuEncoder", runner.Operations[^1]);
    }

    [Fact]
    public async Task CancellationDuringInputInspectionPreservesCancellationAndSource()
    {
        var segment = CreateSegment("canceled-probe.flv");
        var runner = new PendingProbeRunner();
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = Path.Combine(_testDirectory, "canceled-output.mp4");
        var operation = runtime.ConcatAsync([new(1, segment, TimeSpan.FromSeconds(2))], output,
            _ => throw new InvalidOperationException("A canceled inspection cannot start an encoder."),
            allowStreamCopy: true, overwriteDestination: false, cancellationToken: cancellation.Token);
        await runner.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation).ConfigureAwait(true);
        Assert.True(File.Exists(segment));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, ".downkyi-concat-*"));
    }

    private sealed class PendingProbeRunner : IFfmpegProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Assert.Equal("probe-concat-input", command.Operation);
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The canceled probe cannot return a result.");
        }
    }

    [Theory]
    [InlineData(FfmpegOperationFailureKind.ProcessFailure, 2)]
    [InlineData(FfmpegOperationFailureKind.InputInvalid, 1)]
    [InlineData(FfmpegOperationFailureKind.ProcessUnavailable, 1)]
    [InlineData(FfmpegOperationFailureKind.Timeout, 1)]
    public async Task EncodingFallsBackOnlyForRecoverableProcessFailure(
        FfmpegOperationFailureKind failureKind, int attempts)
    {
        var segment = CreateSegment("encode-failure.flv");
        var runner = new InspectedConcatRunner(hardwareFailure: failureKind);
        var runtime = new FfmpegConcatRuntime(runner, new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var hardware = new FfmpegHardwareEncoderProfile(
            DownKyi.Application.Settings.FfmpegHardwareAcceleration.NvidiaNvenc, "NVENC", "h264_nvenc",
            HardwareArguments);
        var output = Path.Combine(_testDirectory, "encoding-result.mp4");
        var result = await runtime.ConcatAsync([new(1, segment)], output,
            _ => Task.FromResult<FfmpegHardwareEncoderProfile?>(hardware),
            allowStreamCopy: true, overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(attempts, runner.Operations.Count);
        Assert.Equal(attempts == 2, result.Succeeded);
        if (attempts == 1)
        {
            Assert.Equal(failureKind, result.FailureKind);
            Assert.False(File.Exists(output));
        }
        Assert.True(File.Exists(segment));
    }

    private sealed class InspectedConcatRunner(
        bool malformed = false, bool failProbe = false, bool differentConfiguration = false,
        bool differentAudio = false,
        FfmpegOperationFailureKind hardwareFailure = FfmpegOperationFailureKind.None) : IFfmpegProcessRunner
    {
        public List<string> Operations { get; } = [];
        public string[] ListLines { get; private set; } = [];
        public FfmpegCommand? MediaCommand { get; private set; }

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Operations.Add(command.Operation);
            if (command.Operation == "probe-concat-input")
            {
                var configuration = differentConfiguration && Operations.Count > 1 ? "different" : "same";
                var audioCodec = Operations.Count > 1 ? "opus" : "aac";
                var audio = differentAudio
                    ? $$""" ,{"codec_type":"audio","codec_name":"{{audioCodec}}","time_base":"1/48000","extradata_hash":"audio"} """
                    : string.Empty;
                var json = malformed ? "{\"streams\":[null]}" : $$"""
                    {"streams":[{"codec_type":"video","codec_name":"h264","time_base":"1/1000","extradata_hash":"{{configuration}}"}{{audio}}]}
                    """;
                return new(!failProbe, failProbe ? 1 : 0, json,
                    failProbe ? "Invalid data found when processing input" : string.Empty, false);
            }

            if (command.Operation == "concat-HardwareEncoder" && hardwareFailure != FfmpegOperationFailureKind.None)
            {
                return new(false, 1, string.Empty,
                    hardwareFailure == FfmpegOperationFailureKind.InputInvalid
                        ? "Invalid data found when processing input" : "Encoder initialization failed.",
                    TimedOut: hardwareFailure == FfmpegOperationFailureKind.Timeout,
                    ProcessStarted: hardwareFailure != FfmpegOperationFailureKind.ProcessUnavailable);
            }

            MediaCommand = command;
            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            ListLines = await File.ReadAllLinesAsync(command.Arguments[inputIndex + 1], cancellationToken)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], cancellationToken)
                .ConfigureAwait(false);
            return new(true, 0, string.Empty, string.Empty, false);
        }
    }

    private sealed class RecordingConcatRunner(bool writeNonEmptyOutput = true) : IFfmpegProcessRunner
    {
        public List<FfmpegCommand> Commands { get; } = new();

        public string[] ConcatListLines { get; private set; } = Array.Empty<string>();

        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            ConcatListLines = await File.ReadAllLinesAsync(
                command.Arguments[inputIndex + 1],
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(
                    command.Arguments[^1],
                    writeNonEmptyOutput ? [1, 2, 3] : [],
                    cancellationToken)
                .ConfigureAwait(false);
            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
        }
    }

    private sealed class FailedConcatRunner : IFfmpegProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.StartsWith("concat-", command.Operation, StringComparison.Ordinal);
            CallCount++;
            return Task.FromResult(new FfmpegProcessResult(
                false,
                1,
                string.Empty,
                "concat failed",
                false));
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
