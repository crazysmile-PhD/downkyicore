using DownKyi.Core.Aria2cNet;
using DownKyi.Core.Aria2cNet.Client;
using DownKyi.Core.Aria2cNet.Server;
using DownKyi.Core.BiliApi.History.Models;
using DownKyi.Core.BiliApi.Users;
using DownKyi.Core.BiliApi.Users.Models;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.FileName;
using DownKyi.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace DownKyi.Core.Tests;

public sealed class EnumValueContractTests
{
    [Fact]
    public void AriaManagerEventsPreservePayloads()
    {
        var manager = new TestAriaManager();
        AriaProgressEventArgs? progress = null;
        AriaDownloadCompletedEventArgs? completed = null;
        AriaGlobalStatusEventArgs? global = null;

        manager.TellStatus += (_, e) => progress = e;
        manager.DownloadFinish += (_, e) => completed = e;
        manager.GlobalStatus += (_, e) => global = e;

        manager.RaiseProgress(100, 40, 12, "gid-1");
        manager.RaiseCompleted(true, "output.mp4", "gid-1", "done");
        manager.RaiseGlobalStatus(25);

        Assert.NotNull(progress);
        Assert.Equal(100, progress.TotalLength);
        Assert.Equal(40, progress.CompletedLength);
        Assert.Equal(12, progress.Speed);
        Assert.Equal("gid-1", progress.Gid);
        Assert.NotNull(completed);
        Assert.True(completed.IsSuccess);
        Assert.Equal("output.mp4", completed.DownloadPath);
        Assert.Equal("gid-1", completed.Gid);
        Assert.Equal("done", completed.Message);
        Assert.NotNull(global);
        Assert.Equal(25, global.Speed);
    }

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

    [Fact]
    public void EnumWireMappingsRemainExplicitAndLowercase()
    {
        Assert.Equal("debug", AriaServer.GetLogLevelArgument(AriaConfigLogLevel.DEBUG));
        Assert.Equal("notice", AriaServer.GetLogLevelArgument(AriaConfigLogLevel.NOTICE));
        Assert.Equal("none", AriaServer.GetFileAllocationArgument(AriaConfigFileAllocation.NONE));
        Assert.Equal("prealloc", AriaServer.GetFileAllocationArgument(AriaConfigFileAllocation.PREALLOC));
        Assert.Equal("pubdate", UserSpace.GetPublicationOrderValue(PublicationOrder.PUBDATE));
        Assert.Equal("click", UserSpace.GetPublicationOrderValue(PublicationOrder.CLICK));
    }

    private sealed class TestAriaManager : AriaManager
    {
        public TestAriaManager()
            : base(new AriaClient(), NullLogger<AriaManager>.Instance)
        {
        }

        public void RaiseProgress(long totalLength, long completedLength, long speed, string gid)
        {
            OnTellStatus(totalLength, completedLength, speed, gid);
        }

        public void RaiseCompleted(bool isSuccess, string? downloadPath, string gid, string? message)
        {
            OnDownloadFinish(isSuccess, downloadPath, gid, message);
        }

        public void RaiseGlobalStatus(long speed)
        {
            OnGlobalStatus(speed);
        }
    }
}
