using DownKyi.Core.FFmpeg;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class FfmpegCommitBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"downkyi-ffmpeg-commit-{Guid.NewGuid():N}");
    private readonly SettingsStore _settings;

    public FfmpegCommitBoundaryTests()
    {
        Directory.CreateDirectory(_directory);
        _settings = new SettingsStore(Path.Combine(_directory, "settings.json"));
    }

    [Fact]
    public async Task DashMergePreservesInputsUntilPipelineCompletionCommits()
    {
        var audio = CreateInput("audio.m4s");
        var video = CreateInput("video.m4s");
        var output = Path.Combine(_directory, "output.mp4");
        var processor = new FfmpegProcessor(
            _settings,
            NullLoggerFactory.Instance,
            new SuccessfulOutputRunner());

        var succeeded = await processor.MergeVideoAsync(
            _settings.Current.Video,
            audio,
            video,
            output,
            overwriteDestination: false,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(succeeded);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(audio));
        Assert.True(File.Exists(video));
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateInput(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private sealed class SuccessfulOutputRunner : IFfmpegProcessRunner
    {
        public async Task<FfmpegProcessResult> RunAsync(
            FfmpegCommand command,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(
                command.Arguments[^1],
                [1, 2, 3],
                cancellationToken).ConfigureAwait(false);
            return new FfmpegProcessResult(
                Succeeded: true,
                ExitCode: 0,
                StandardOutput: string.Empty,
                StandardError: string.Empty,
                TimedOut: false);
        }
    }
}
