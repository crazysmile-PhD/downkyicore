using DownKyi.Core.FFMpeg;

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
    public async Task ConcatSortsDurlSegmentsAndAtomicallyFinalizesValidatedOutput()
    {
        var first = CreateSegment("first.flv");
        var second = CreateSegment("second.flv");
        var runner = new RecordingConcatRunner();
        var validator = new StubMediaValidator(isValid: true);
        var runtime = new FfmpegConcatRuntime(runner, validator, () => 1);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [
                new FfmpegConcatSegment(2, second, TimeSpan.FromSeconds(5)),
                new FfmpegConcatSegment(1, first, TimeSpan.FromSeconds(5))
            ],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
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
        var runtime = new FfmpegConcatRuntime(runner, validator, () => 1);
        var output = Path.Combine(_testDirectory, "result.mp4");

        var result = await runtime.ConcatAsync(
            [new FfmpegConcatSegment(1, segment, TimeSpan.FromSeconds(5))],
            output,
            hardwareEncoder: null,
            allowStreamCopy: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(output));
        Assert.True(File.Exists(segment));
        Assert.Empty(Directory.EnumerateFiles(_testDirectory, "*.partial.mp4"));
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
            var inputIndex = command.Arguments.ToList().IndexOf("-i");
            ConcatListLines = await File.ReadAllLinesAsync(
                command.Arguments[inputIndex + 1],
                cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], cancellationToken)
                .ConfigureAwait(false);
            return new FfmpegProcessResult(true, 0, string.Empty, string.Empty, false);
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
