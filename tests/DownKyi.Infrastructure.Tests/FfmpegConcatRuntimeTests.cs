using DownKyi.Application.Media;
using DownKyi.Infrastructure.FFmpeg;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Infrastructure.Tests;

public sealed class FfmpegConcatRuntimeTests : IDisposable
{
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
            hardwareEncoder: null,
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
            hardwareEncoder: null,
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
            hardwareEncoder: null,
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
            hardwareEncoder: null,
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
