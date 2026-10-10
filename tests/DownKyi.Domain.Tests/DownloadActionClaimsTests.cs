using System.Collections.Immutable;
using DownKyi.Domain.Downloads;

namespace DownKyi.Domain.Tests;

public sealed class DownloadActionClaimsTests
{
    [Fact]
    public void ContentSelectionProducesPhysicalActionClaims()
    {
        var selection = new DownloadContentSelection(
            Audio: true,
            Video: true,
            Danmaku: true,
            Subtitle: true,
            Cover: true)
        {
            SelectedSubtitleTrackIds = ImmutableArray.Create(22L),
            DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
        };

        var claims = selection.ActionClaims;

        Assert.True(claims.Contains(DownloadActionClaim.Media));
        Assert.True(claims.Contains(DownloadActionClaim.Subtitle));
        Assert.True(claims.Contains(DownloadActionClaim.DanmakuAss));
        Assert.False(claims.Contains(DownloadActionClaim.DanmakuXml));
        Assert.True(claims.Contains(DownloadActionClaim.Cover));
    }

    [Fact]
    public void ClaimsUseOneMediaAndSubtitleOutputButDistinctDanmakuFormats()
    {
        var audio = new DownloadContentSelection(true, false, false, false, false);
        var video = new DownloadContentSelection(false, true, false, false, false);
        var firstSubtitle = new DownloadContentSelection(false, false, false, true, false)
        {
            SelectedSubtitleTrackIds = ImmutableArray.Create(1L)
        };
        var secondSubtitle = firstSubtitle with
        {
            SelectedSubtitleTrackIds = ImmutableArray.Create(2L)
        };
        var ass = new DownloadContentSelection(false, false, true, false, false)
        {
            DanmakuOutputFormat = DownloadDanmakuOutputFormat.Ass
        };
        var xml = ass with { DanmakuOutputFormat = DownloadDanmakuOutputFormat.Xml };

        Assert.True(audio.ActionClaims.Overlaps(video.ActionClaims));
        Assert.True(firstSubtitle.ActionClaims.Overlaps(secondSubtitle.ActionClaims));
        Assert.False(ass.ActionClaims.Overlaps(xml.ActionClaims));
    }

    [Fact]
    public void CoveredClaimsSubtractOnlyTheirPhysicalActions()
    {
        var covered = DownloadActionClaims.Media
            .Union(DownloadActionClaims.Subtitle)
            .Union(DownloadActionClaims.FromDanmaku(DownloadDanmakuOutputFormat.Ass))
            .Union(DownloadActionClaims.Cover);

        var remaining = covered.SubtractFrom(DownloadContentSelection.All);

        Assert.False(remaining.Audio);
        Assert.False(remaining.Video);
        Assert.False(remaining.Subtitle);
        Assert.False(remaining.Cover);
        Assert.True(remaining.Danmaku);
        Assert.Equal(DownloadDanmakuOutputFormat.Xml, remaining.DanmakuOutputFormat);
    }

    [Fact]
    public void EmptySubtitleSelectionDoesNotOwnAnOutputClaim()
    {
        var selection = new DownloadContentSelection(false, false, false, true, false)
        {
            SelectedSubtitleTrackIds = ImmutableArray<long>.Empty
        };

        Assert.False(selection.ActionClaims.HasAny);
    }

    [Fact]
    public void NfoClaimIsDerivedFromThePlanOutputRatherThanContentSelection()
    {
        var content = DownloadContentSelection.None with { Video = true };

        var withoutNfo = DownloadActionClaims.From(content, includesNfo: false);
        var withNfo = DownloadActionClaims.From(content, includesNfo: true);

        Assert.False(withoutNfo.Contains(DownloadActionClaim.Nfo));
        Assert.True(withNfo.Contains(DownloadActionClaim.Nfo));
        Assert.True(withNfo.Overlaps(DownloadActionClaims.Nfo));
    }
}
