using DownKyi.Services.Media;

namespace DownKyi.Desktop.Tests;

public sealed class ContentDownloadNotificationFormatterTests
{
    [AvaloniaFact]
    public Task AddedAndSkippedCountsProduceClearSummaries()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();

            Assert.Equal(
                "没有选中项符合下载要求！",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(0, 0)));
            Assert.Equal(
                "成功添加了2项~",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(2, 0)));
            Assert.Equal(
                "新增 2 项；重复 0 项，跳过 1 项，失败 0 项。",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(2, 1)));
            Assert.Equal(
                "跳过了 1 个不可用项目，没有添加下载项。",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(0, 1)));
            Assert.Equal(
                "此内容已加入下载队列，无需重复添加。",
                ContentDownloadNotificationFormatter.Format(
                    new ContentDownloadBatchResult(0, 0, DuplicateCount: 1)));
            Assert.Equal(
                "新增下载任务失败。",
                ContentDownloadNotificationFormatter.Format(
                    new ContentDownloadBatchResult(0, 0, FailedCount: 1)));
            Assert.Equal(
                "新增 2 项；重复 1 项，跳过 1 项，失败 1 项。",
                ContentDownloadNotificationFormatter.Format(
                    new ContentDownloadBatchResult(
                        AddedCount: 2,
                        SkippedCount: 1,
                        DuplicateCount: 1,
                        FailedCount: 1)));
            Assert.Equal(
                "未新增任务；重复 1 项，跳过 1 项，失败 1 项。",
                ContentDownloadNotificationFormatter.Format(
                    new ContentDownloadBatchResult(
                        AddedCount: 0,
                        SkippedCount: 1,
                        DuplicateCount: 1,
                        FailedCount: 1)));
        });
    }
}
