using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;
using static DownKyi.Tests.DownloadPipelineTestSupport;

namespace DownKyi.Tests;

public sealed class MuxAndFinalizeStageTests
{
    [Theory]
    [InlineData(30280, ".mp3", false)]
    [InlineData(30280, ".aac", false)]
    [InlineData(30280, ".m4a", true)]
    [InlineData(30251, ".m4a", false)]
    [InlineData(30251, ".flac", true)]
    public async Task ResumeOnlyReusesStagedMediaInTheExpectedOriginalCodecFormat(
        int audioCodecId,
        string extension,
        bool expectedReuse)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-audio-resume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new TestSettingsStore();
            var context = CreateContext(
                settings.Store.Current,
                requestedContent: DownloadContentSelection.None with { Audio = true },
                audioCodecId: audioCodecId);
            context.StagingDirectory = directory;
            var stagedFile = context.WorkingBasePath + extension;
            await File.WriteAllBytesAsync(stagedFile, [1, 2, 3, 4], TestContext.Current.CancellationToken);

            Assert.Equal(expectedReuse, context.TryReuseStagedMedia());
            Assert.Equal(expectedReuse ? stagedFile : null, context.OutputMedia);
            Assert.True(File.Exists(stagedFile));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OutputLayoutSelectsDashPathFromRequestedStreamShape()
    {
        using var settings = new TestSettingsStore();
        var videoContext = CreateContext(settings.Store.Current);
        videoContext.VideoFile = "video-stream";
        Assert.EndsWith(
            ".mp4",
            DownloadOutputLayout.GetDashMediaPath(
                videoContext.WorkingBasePath,
                hasVideo: true,
                audioCodecId: videoContext.Input.Metadata.AudioCodec.Id),
            StringComparison.Ordinal);

        var audioContext = CreateContext(
            settings.Store.Current,
            requestedContent: DownloadContentSelection.None with { Audio = true });
        Assert.EndsWith(
            ".m4a",
            DownloadOutputLayout.GetDashMediaPath(
                audioContext.WorkingBasePath,
                hasVideo: false,
                audioCodecId: audioContext.Input.Metadata.AudioCodec.Id),
            StringComparison.Ordinal);

        var losslessContext = CreateContext(
            settings.Store.Current,
            audioCodecId: 30251);
        Assert.EndsWith(
            ".flac",
            DownloadOutputLayout.GetDashMediaPath(
                losslessContext.WorkingBasePath,
                hasVideo: false,
                audioCodecId: losslessContext.Input.Metadata.AudioCodec.Id),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FinalizeStageUsesInjectedClockForCompletionSummary()
    {
        var finishedAt = new DateTimeOffset(
            2026,
            7,
            26,
            1,
            2,
            3,
            TimeSpan.Zero);

        var completion = FinalizeStage.CreateCompletionSummary(
            maximumBytesPerSecond: 1_250_000,
            timeProvider: new FixedTimeProvider(finishedAt));

        Assert.Equal(finishedAt.ToUnixTimeSeconds(), completion.FinishedTimestamp);
        Assert.False(string.IsNullOrEmpty(completion.FinishedTimeText));
        Assert.False(string.IsNullOrEmpty(completion.MaximumSpeedText));
    }

}
