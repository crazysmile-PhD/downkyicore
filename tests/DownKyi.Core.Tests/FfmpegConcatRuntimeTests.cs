using DownKyi.Core.FFmpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

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
    public async Task ConcatFinalizesValidatedOutputWithoutDeletingRetryInputs()
    {
        var first = CreateSegment("first.flv");
        var second = CreateSegment("second.flv");
        var runner = new RecordingConcatRunner();
        var validator = new StubMediaValidator(isValid: true);
        var runtime = new FfmpegConcatRuntime(
            runner,
            validator,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second, TimeSpan.FromSeconds(5)),
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(5))
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.DoesNotContain("copy", runner.Commands[0].Arguments);
        Assert.True(runner.ConcatListLines[0].Contains("first.flv", StringComparison.Ordinal));
        Assert.True(runner.ConcatListLines[1].Contains("second.flv", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcatDeletesRejectedOutputAndReturnsFailure()
    {
        var segment = CreateSegment("bad.flv");
        var runner = new RecordingConcatRunner();
        var validator = new StubMediaValidator(isValid: false);
        var runtime = new FfmpegConcatRuntime(
            runner,
            validator,
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
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
            new StubMediaValidator(isValid: true),
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "owned-by-another-task.mp4");
        byte[] existingContent = [9, 8, 7];
        await File.WriteAllBytesAsync(output, existingContent, TestContext.Current.CancellationToken);

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.DestinationConflict, result.FailureKind);
        Assert.Empty(result.InputFailures);
        Assert.Equal(existingContent, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(segment));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.partial.mp4"));
    }

    [Fact]
    public async Task ConcatValidationFailurePreservesExistingDestination()
    {
        var segment = CreateSegment("invalid.flv");
        var runner = new RecordingConcatRunner();
        var runtime = new FfmpegConcatRuntime(
            runner,
            new StubMediaValidator(isValid: false),
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);
        var output = Path.Combine(_testDirectory, "existing.mp4");
        byte[] existingContent = [6, 5, 4];
        await File.WriteAllBytesAsync(output, existingContent, TestContext.Current.CancellationToken);

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(existingContent, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(segment));
    }

    [Fact]
    public async Task ConcatFailureIdentifiesOnlyCorruptMiddleSegment()
    {
        var first = CreateSegment("diagnostic-first.flv");
        var corrupt = CreateSegment("diagnostic-corrupt.flv");
        var third = CreateSegment("diagnostic-third.flv");
        var runner = new CorruptSegmentRunner(corrupt);
        var runtime = new FfmpegConcatRuntime(
            runner,
            new StubMediaValidator(isValid: false),
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(5)),
                new FfmpegConcatSegment(2, corrupt, TimeSpan.FromSeconds(5)),
                new FfmpegConcatSegment(3, third, TimeSpan.FromSeconds(5))
            ],
            Path.Combine(_testDirectory, "diagnostic-output.mp4"),
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InvalidInput, result.FailureKind);
        Assert.Equal(corrupt, Assert.Single(result.InvalidInputPaths));
        Assert.Equal(
            FfmpegInputFailureKind.DecodeCorruption,
            Assert.Single(result.InputFailures, failure => failure.Path == corrupt).Kind);
        Assert.Equal([first, corrupt, third], runner.ValidatedInputs);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(corrupt));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public async Task ConcatDirectoryInputIsNotMisclassifiedAsMissingMedia()
    {
        var directoryInput = Path.Combine(_testDirectory, "directory-segment.flv");
        Directory.CreateDirectory(directoryInput);
        var runner = new RecordingConcatRunner();
        var runtime = new FfmpegConcatRuntime(
            runner,
            new StubMediaValidator(isValid: true),
            new AsyncConcurrencyGate(() => 1),
            NullLogger<FfmpegConcatRuntime>.Instance);

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, directoryInput, TimeSpan.FromSeconds(5))],
            Path.Combine(_testDirectory, "directory-segment-output.mp4"),
            hardwareEncoder: null,
            allowStreamCopy: false,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(FfmpegOperationFailureKind.InputAccess, result.FailureKind);
        Assert.Empty(result.InvalidInputPaths);
        Assert.Equal(
            FfmpegInputFailureKind.UnsupportedFileType,
            Assert.Single(result.InputFailures).Kind);
        Assert.Empty(runner.Commands);
        Assert.True(Directory.Exists(directoryInput));
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

    private sealed class RecordingConcatRunner : IFfmpegProcessRunner
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
            if (command.Operation == "validate-input")
            {
                return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
            }

            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            ConcatListLines = await File.ReadAllLinesAsync(
                command.Arguments[inputIndex + 1],
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], cancellationToken)
                .ConfigureAwait(false);
            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
        }
    }

    private sealed class CorruptSegmentRunner(string corruptSegment) : IFfmpegProcessRunner
    {
        public List<string> ValidatedInputs { get; } = [];

        public Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.Operation.StartsWith("concat-", StringComparison.Ordinal))
            {
                return Task.FromResult(new FfmpegProcessResult(
                    false,
                    1,
                    string.Empty,
                    "concat failed",
                    false));
            }

            Assert.Equal("validate-input", command.Operation);
            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            var input = command.Arguments[inputIndex + 1];
            ValidatedInputs.Add(input);
            return Task.FromResult(string.Equals(input, corruptSegment, StringComparison.Ordinal)
                ? new FfmpegProcessResult(
                    false,
                    1,
                    string.Empty,
                    "Error while decoding stream #0:0: Invalid data found when processing input",
                    false)
                : new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false));
        }
    }

    private sealed class StubMediaValidator : IFfmpegMediaValidator
    {
        private readonly bool _isValid;

        public StubMediaValidator(bool isValid)
        {
            _isValid = isValid;
        }

        public Task<FfmpegMediaValidationResult> ValidateAsync(
            string mediaFile,
            TimeSpan expectedDuration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_isValid
                ? new FfmpegMediaValidationResult(true, expectedDuration, null)
                : FfmpegMediaValidationResult.Failure("Rejected by test validator."));
        }
    }
}
