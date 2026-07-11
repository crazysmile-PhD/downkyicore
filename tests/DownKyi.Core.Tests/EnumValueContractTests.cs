using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.History.Models;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;

namespace DownKyi.Core.Tests;

public sealed class EnumValueContractTests
{
    [Fact]
    public void NoneMembersDoNotShiftPersistedOrProtocolValues()
    {
        Assert.Equal(0, (int)HowChangePosition.None);
        Assert.Equal(1, (int)HowChangePosition.PosSet);
        Assert.Equal("POS_SET", AriaClient.GetChangePositionValue(HowChangePosition.PosSet));
        Assert.Equal("POS_CUR", AriaClient.GetChangePositionValue(HowChangePosition.PosCurrent));
        Assert.Equal("POS_END", AriaClient.GetChangePositionValue(HowChangePosition.PosEnd));
        Assert.Equal(0, (int)AriaConfigLogLevel.NotSet);
        Assert.Equal(0, (int)AriaConfigFileAllocation.NotSet);
        Assert.Equal(0, (int)DownloadResult.None);
        Assert.Equal(1, (int)DownloadResult.SUCCESS);
        Assert.Equal(0, (int)HistoryBusiness.None);
        Assert.Equal(1, (int)HistoryBusiness.ARCHIVE);
        Assert.Equal(4, (int)HistoryBusiness.ArticleList);
        Assert.Equal(0, (int)BangumiType.None);
        Assert.Equal(1, (int)BangumiType.ANIME);
        Assert.Equal(0, (int)FollowingOrder.None);
        Assert.Equal(1, (int)FollowingOrder.DEFAULT);
        Assert.Equal(0, (int)PublicationOrder.None);
        Assert.Equal(1, (int)PublicationOrder.PUBDATE);
        Assert.Equal(0, (int)PlayStreamType.None);
        Assert.Equal(1, (int)PlayStreamType.Video);
        Assert.Equal(0, (int)FileNamePart.None);
        Assert.Equal(1, (int)FileNamePart.Order);
        Assert.Equal(100, (int)FileNamePart.Slash);
        Assert.Equal(0, (int)ThemeMode.None);
        Assert.Equal(1, (int)ThemeMode.Default);
    }
}
