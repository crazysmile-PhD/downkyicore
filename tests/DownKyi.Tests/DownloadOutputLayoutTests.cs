using DownKyi.Application.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Services.Download;

namespace DownKyi.Tests;

public sealed class DownloadOutputLayoutTests
{
    private static readonly string BasePath = Path.Combine(
        Path.GetTempPath(),
        "downkyi-output-layout",
        "video");

    [Theory]
    [InlineData(true, 30251, ".mp4")]
    [InlineData(false, 30251, ".flac")]
    [InlineData(false, 30250, ".mp4")]
    [InlineData(false, 30280, ".m4a")]
    [InlineData(false, 30232, ".m4a")]
    [InlineData(false, 30216, ".m4a")]
    public void DashMediaPathPreservesStreamAndCodecRules(
        bool hasVideo,
        int audioCodecId,
        string expectedExtension)
    {
        var path = DownloadOutputLayout.GetDashMediaPath(
            BasePath,
            hasVideo,
            audioCodecId);

        Assert.Equal(BasePath + expectedExtension, path);
    }

    [Fact]
    public void DurlMediaPathIsAlwaysMp4()
    {
        Assert.Equal(BasePath + ".mp4", DownloadOutputLayout.GetDurlMediaPath(BasePath));
    }

    [Theory]
    [InlineData(30280, ".m4a")]
    [InlineData(30251, ".flac")]
    [InlineData(30250, ".mp4")]
    public void ReusableStagedMediaMustMatchTheOriginalCodecOutput(int audioCodecId, string extension)
    {
        Assert.Equal(
            [BasePath + ".mp4"],
            DownloadOutputLayout.GetReusableStagedMediaPaths(
                BasePath,
                needsVideo: true,
                needsAudio: true,
                audioCodecId));
        Assert.Equal(
            [BasePath + extension],
            DownloadOutputLayout.GetReusableStagedMediaPaths(
                BasePath,
                needsVideo: false,
                needsAudio: true,
                audioCodecId));
        Assert.Empty(DownloadOutputLayout.GetReusableStagedMediaPaths(
            BasePath,
            needsVideo: false,
                needsAudio: false,
                audioCodecId));
    }

    [Theory]
    [InlineData(DanmakuOutputFormat.Ass, true, ".ass")]
    [InlineData(DanmakuOutputFormat.Xml, false, ".xml")]
    public void SingleDanmakuFormatHasOneExpectedPath(
        DanmakuOutputFormat format,
        bool expectsAss,
        string expectedExtension)
    {
        var output = Assert.Single(DownloadOutputLayout.GetDanmakuOutputPaths(BasePath, format));

        Assert.Equal(
            expectsAss ? DownloadDanmakuOutputKind.Ass : DownloadDanmakuOutputKind.Xml,
            output.Kind);
        Assert.Equal(BasePath + expectedExtension, output.File);
    }

    [Fact]
    public void CombinedDanmakuFormatPreservesAssThenXmlOrder()
    {
        Assert.Equal(
            [
                new DownloadDanmakuOutputPath(DownloadDanmakuOutputKind.Ass, BasePath + ".ass"),
                new DownloadDanmakuOutputPath(DownloadDanmakuOutputKind.Xml, BasePath + ".xml")
            ],
            DownloadOutputLayout.GetDanmakuOutputPaths(
                BasePath,
                DanmakuOutputFormat.AssAndXml));
    }

    [Fact]
    public void SubtitleAndNfoPathsPreserveExistingNames()
    {
        const string languageDescription = "繁體 中文";

        Assert.Equal(
            $"{BasePath}_{languageDescription}.srt",
            DownloadOutputLayout.GetSubtitleTrackPath(BasePath, languageDescription));
        Assert.Equal(
            BasePath + ".srt",
            DownloadOutputLayout.GetDefaultSubtitlePath(BasePath));
        Assert.Equal(BasePath + ".nfo", DownloadOutputLayout.GetNfoPath(BasePath));
    }

    [Theory]
    [InlineData("https://i0.example.invalid/cover.jpg?token=redacted#preview", "jpg")]
    [InlineData("//i0.example.invalid/cover.webp@672w_378h.webp?token=redacted", "webp")]
    [InlineData("images/cover.png#thumbnail", "png")]
    [InlineData("https://i0.example.invalid/cover", "")]
    [InlineData("   ", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void CoverPathsPreserveSourceExtensionCharacterization(
        string? sourceAddress,
        string expectedExtension)
    {
        Assert.Equal(
            expectedExtension,
            DownloadOutputLayout.GetSourceExtension(sourceAddress));
        Assert.Equal(
            $"{BasePath}.{expectedExtension}",
            DownloadOutputLayout.GetPageCoverPath(BasePath, sourceAddress));
        Assert.Equal(
            $"{BasePath}.Cover.{expectedExtension}",
            DownloadOutputLayout.GetMainCoverPath(BasePath, sourceAddress));
    }

    [Theory]
    [InlineData(".mp4", true, false, false)]
    [InlineData(".MP3", true, true, false)]
    [InlineData(".aac", true, true, false)]
    [InlineData(".M4A", true, true, false)]
    [InlineData(".flac", true, true, false)]
    [InlineData(".jpg", false, false, true)]
    [InlineData(".jpeg", false, false, true)]
    [InlineData(".png", false, false, true)]
    [InlineData(".webp", false, false, true)]
    [InlineData(".bmp", false, false, true)]
    [InlineData(".gif", false, false, true)]
    [InlineData(".avif", false, false, true)]
    [InlineData(".srt", false, false, false)]
    public void ExtensionClassificationHasOneTruthTable(
        string extension,
        bool isMedia,
        bool isAudio,
        bool isImage)
    {
        Assert.Equal(isMedia, DownloadOutputLayout.IsMediaExtension(extension));
        Assert.Equal(isAudio, DownloadOutputLayout.IsAudioExtension(extension));
        Assert.Equal(isImage, DownloadOutputLayout.IsImageExtension(extension));
    }

    [Theory]
    [InlineData(DownloadActionClaim.Media, ".mp4", true)]
    [InlineData(DownloadActionClaim.Media, ".flac", true)]
    [InlineData(DownloadActionClaim.Media, ".m4a", true)]
    [InlineData(DownloadActionClaim.Subtitle, ".srt", true)]
    [InlineData(DownloadActionClaim.Subtitle, "_English.srt", true)]
    [InlineData(DownloadActionClaim.DanmakuAss, ".ass", true)]
    [InlineData(DownloadActionClaim.DanmakuXml, ".xml", true)]
    [InlineData(DownloadActionClaim.Nfo, ".nfo", true)]
    [InlineData(DownloadActionClaim.Cover, ".jpg", true)]
    [InlineData(DownloadActionClaim.Cover, ".Cover.avif", true)]
    [InlineData(DownloadActionClaim.Media, "_English.srt", false)]
    [InlineData(DownloadActionClaim.Subtitle, "-English.srt", false)]
    [InlineData(DownloadActionClaim.Cover, ".Cover.txt", false)]
    public void OutputClaimConflictUsesPhysicalLayout(
        DownloadActionClaim claim,
        string outputSuffix,
        bool expected)
    {
        var conflict = DownloadOutputLayout.HasOutputClaimConflict(
            BasePath,
            BasePath + outputSuffix,
            new DownloadActionClaims(claim));

        Assert.Equal(expected, conflict);
    }

}
