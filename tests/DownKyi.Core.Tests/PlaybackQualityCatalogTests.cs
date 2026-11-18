using DownKyi.Core.BiliApi.BiliUtils;

namespace DownKyi.Core.Tests;

public sealed class PlaybackQualityCatalogTests
{
    [Fact]
    public void LargeView4KIsAvailableAsADistinctPreference()
    {
        var options = PlaybackQualityCatalog.GetResolutions();

        Assert.Contains(options, option => option.Id == 120 && option.Name == "4K 超清");
        Assert.Contains(options, option => option.Id == 122 && option.Name == "大视界 4K");
    }

    [Theory]
    [InlineData(120, PlaybackQualityMatchKind.Higher)]
    [InlineData(122, PlaybackQualityMatchKind.Exact)]
    [InlineData(125, PlaybackQualityMatchKind.Lower)]
    [InlineData(127, PlaybackQualityMatchKind.Lower)]
    public void LargeView4KParticipatesInQualityRanking(
        int requestedQuality,
        PlaybackQualityMatchKind expectedKind)
    {
        var match = PlaybackQualityCatalog.SelectVideoQuality([122, 80], requestedQuality);

        Assert.Equal(122, match.SelectedQuality);
        Assert.Equal(expectedKind, match.Kind);
    }
}
