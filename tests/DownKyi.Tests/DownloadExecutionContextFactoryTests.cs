using DownKyi.Application.Downloads;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Core.BiliApi.VideoStream;
using DownKyi.Core.BiliApi.VideoStream.Models;
using DownKyi.Core.Settings;
using DownKyi.Domain.Downloads;
using DownKyi.Infrastructure.Downloads;
using DownKyi.Infrastructure.Time;
using DownKyi.Models;
using DownKyi.Services.Download;
using DownKyi.ViewModels.DownloadManager;
using Microsoft.Data.Sqlite;

namespace DownKyi.Tests;

public sealed class DownloadExecutionContextFactoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "downkyi-execution-input-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateUsesDomainSnapshotAndCapturesSettingsAndInitialPlayback()
    {
        Directory.CreateDirectory(_directory);
        using var settings = new TestSettingsStore();
        var originalSettings = settings.Store.Update(current => current with
        {
            Basic = current.Basic with { DownloadFinishedSort = DownloadFinishedSort.Number },
            Video = current.Video with
            {
                VideoParseType = 1,
                IsTranscodingAacToMp3 = AllowStatus.Yes
            },
            Danmaku = current.Danmaku with
            {
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                FontName = "original-font"
            }
        });
        var originalContent = new DownloadContentSelection(true, false, true, false, true);
        var originalPlayUrl = new PlayUrl();
        var downloadBase = new DownloadBase
        {
            Id = "execution-input",
            NeedDownloadContent = originalContent,
            Bvid = "BV-original",
            Avid = 11,
            Cid = 22,
            EpisodeId = 33,
            Page = 4,
            Order = 5,
            FilePath = Path.Combine(_directory, "original-output"),
            Resolution = new Quality { Id = 80, Name = "1080P" },
            AudioCodec = new Quality { Id = 30280, Name = "192K" },
            VideoCodecName = "AVC"
        };
        var admitted = new DownloadingItem
        {
            DownloadBase = downloadBase,
            Downloading = new Downloading
            {
                Id = downloadBase.Id,
                DownloadBase = downloadBase,
                DownloadStatus = DownloadStatus.NotStarted,
                PlayStreamType = PlayStreamType.Bangumi
            },
            PlayUrl = originalPlayUrl
        };
        using var store = new SqliteDownloadTaskStore(
            new SqliteDownloadTaskStoreOptions(Path.Combine(_directory, "download.db")),
            new SystemClock());
        var clock = new SystemClock();
        using var tasks = new DownloadTaskApplicationService(store, clock);
        using var projections = new DownloadTaskProjectionStore(tasks, clock);
        var stateWriter = new DownloadTaskStateWriter(tasks);
        await projections.AddDownloadingAsync(admitted, TestContext.Current.CancellationToken);
        var taskId = new DownloadTaskId(downloadBase.Id);
        await stateWriter.StartAsync(taskId, TestContext.Current.CancellationToken);
        var activeProjection = projections.GetRequiredDownloadingProjection(taskId);

        var replacementContent = DownloadContentSelection.None with { Video = true };
        activeProjection.DownloadBase.NeedDownloadContent = replacementContent;
        activeProjection.DownloadBase.Bvid = "BV-replacement";
        activeProjection.DownloadBase.Avid = 111;
        activeProjection.DownloadBase.Cid = 222;
        activeProjection.DownloadBase.EpisodeId = 333;
        activeProjection.DownloadBase.Page = 44;
        activeProjection.DownloadBase.Order = 55;
        activeProjection.DownloadBase.FilePath = Path.Combine(_directory, "replacement-output");
        activeProjection.DownloadBase.Resolution = new Quality { Id = 120, Name = "4K" };
        activeProjection.DownloadBase.AudioCodec = new Quality { Id = 30251, Name = "Hi-Res" };
        activeProjection.DownloadBase.VideoCodecName = "HEVC";
        activeProjection.Downloading.PlayStreamType = PlayStreamType.Cheese;

        Assert.Equal(replacementContent, activeProjection.DownloadBase.NeedDownloadContent);
        Assert.Equal("BV-replacement", activeProjection.DownloadBase.Bvid);
        Assert.Equal(111, activeProjection.DownloadBase.Avid);
        Assert.Equal(222, activeProjection.DownloadBase.Cid);
        Assert.Equal(333, activeProjection.DownloadBase.EpisodeId);
        Assert.Equal(44, activeProjection.DownloadBase.Page);
        Assert.Equal(55, activeProjection.DownloadBase.Order);
        Assert.EndsWith("replacement-output", activeProjection.DownloadBase.FilePath, StringComparison.Ordinal);
        Assert.Equal(120, activeProjection.DownloadBase.Resolution.Id);
        Assert.Equal("4K", activeProjection.DownloadBase.Resolution.Name);
        Assert.Equal(30251, activeProjection.DownloadBase.AudioCodec.Id);
        Assert.Equal("Hi-Res", activeProjection.DownloadBase.AudioCodec.Name);
        Assert.Equal("HEVC", activeProjection.DownloadBase.VideoCodecName);
        Assert.Equal(PlayStreamType.Cheese, activeProjection.Downloading.PlayStreamType);
        Assert.Same(originalPlayUrl, activeProjection.PlayUrl);

        var context = new DownloadExecutionContextFactory(projections, settings.Store).Create(taskId);

        var replacementPlayUrl = new PlayUrl();
        activeProjection.PlayUrl = replacementPlayUrl;
        var replacementSettings = settings.Store.Update(current => current with
        {
            Basic = current.Basic with { DownloadFinishedSort = DownloadFinishedSort.DownloadDesc },
            Video = current.Video with
            {
                VideoParseType = 0,
                IsTranscodingAacToMp3 = AllowStatus.No
            },
            Danmaku = current.Danmaku with
            {
                ScreenWidth = 1280,
                ScreenHeight = 720,
                FontName = "replacement-font"
            }
        });

        Assert.Same(replacementPlayUrl, activeProjection.PlayUrl);
        Assert.Equal(DownloadFinishedSort.DownloadDesc, replacementSettings.Basic.DownloadFinishedSort);
        Assert.Equal(0, replacementSettings.Video.VideoParseType);
        Assert.Equal(AllowStatus.No, replacementSettings.Video.IsTranscodingAacToMp3);
        Assert.Equal(1280, replacementSettings.Danmaku.ScreenWidth);
        Assert.Equal(720, replacementSettings.Danmaku.ScreenHeight);
        Assert.Equal("replacement-font", replacementSettings.Danmaku.FontName);

        Assert.Equal(originalContent, context.Input.RequestedContent);
        Assert.True(context.NeedsAudio);
        Assert.False(context.NeedsVideo);
        Assert.True(context.NeedsDanmaku);
        Assert.False(context.NeedsSubtitle);
        Assert.True(context.NeedsCover);
        Assert.Equal(
            new DownloadMediaIdentity("BV-original", 11, 22, 33, 4, 5),
            context.Input.Metadata.Media);
        Assert.EndsWith("original-output", context.Input.OutputBasePath, StringComparison.Ordinal);
        Assert.Equal(new DownloadQuality(80, "1080P"), context.Input.Metadata.Resolution);
        Assert.Equal(new DownloadQuality(30280, "192K"), context.Input.Metadata.AudioCodec);
        Assert.Equal("AVC", context.Input.Metadata.VideoCodecName);
        Assert.Equal(PlayStreamType.Bangumi, context.Input.StreamType);
        Assert.Same(originalPlayUrl, context.PlayUrl);
        Assert.NotSame(replacementPlayUrl, context.PlayUrl);
        Assert.Equal(DownloadFinishedSort.Number, context.Input.FinishedSort);
        Assert.Equal(originalSettings.Video, context.Input.VideoSettings);
        Assert.Equal(originalSettings.Danmaku, context.Input.DanmakuSettings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "download.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = true,
                DefaultTimeout = 5
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
