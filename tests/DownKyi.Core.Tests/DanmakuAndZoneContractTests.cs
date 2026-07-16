using System.Text;
using DownKyi.Core.BiliApi.Zone;
using DownKyi.Core.Danmaku2Ass;
using DanmakuBilibili = DownKyi.Core.Danmaku2Ass.BilibiliDanmakuConverter;

namespace DownKyi.Core.Tests;

public sealed class DanmakuAndZoneContractTests
{
    [Fact]
    public void ZoneImageLookupUsesKnownAndFallbackKeys()
    {
        var icons = VideoZoneIcon.Instance();

        Assert.Equal("Zone.techDrawingImage", icons.GetZoneImageKey(36));
        Assert.Equal("videoUpDrawingImage", icons.GetZoneImageKey(int.MaxValue));
    }

    [Theory]
    [InlineData(6, 426, 240)]
    [InlineData(64, 1280, 720)]
    [InlineData(80, 1920, 1080)]
    [InlineData(120, 3840, 2160)]
    [InlineData(-1, 0, 0)]
    public void ResolutionLookupReturnsExpectedDimensions(int quality, int expectedWidth, int expectedHeight)
    {
        var resolution = DanmakuBilibili.GetResolution(quality);

        Assert.Equal(expectedWidth, resolution["width"]);
        Assert.Equal(expectedHeight, resolution["height"]);
    }

    [Fact]
    public void StudioUsesInjectedOutputEncoding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"downkyi-studio-{Guid.NewGuid():N}.txt");
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var studio = new Studio(new Config(), new List<Danmaku>(), encoding);

        try
        {
            studio.CreateFile(path, "字幕");

            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
            Assert.Equal("字幕", File.ReadAllText(path, encoding));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StudioDoesNotHideOutputWriteFailures()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-studio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var studio = new Studio(new Config(), [], Encoding.UTF8);

        try
        {
            var exception = Record.Exception(() => studio.CreateFile(directory, "subtitle"));

            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected a visible file-system failure, got {exception?.GetType().Name ?? "no exception"}.");
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void ProducerReportReturnsSummaryAndPerFilterCounts()
    {
        var producer = new Producer(
            new Dictionary<string, bool>
            {
                ["top_filter"] = false,
                ["bottom_filter"] = false,
                ["scroll_filter"] = false
            },
            []);

        producer.StartHandle();
        var report = producer.Report();

        Assert.Equal(0, report["blocked"]);
        Assert.Equal(0, report["passed"]);
        Assert.Equal(0, report["total"]);
        Assert.Equal(0, report["top_filter"]);
        Assert.Equal(0, report["bottom_filter"]);
        Assert.Equal(0, report["scroll_filter"]);
    }
}
