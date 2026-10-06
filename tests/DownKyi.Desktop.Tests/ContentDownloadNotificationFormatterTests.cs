using Avalonia.Headless.XUnit;
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
                "成功添加了 2 项，跳过了 1 个不可用项目。",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(2, 1)));
            Assert.Equal(
                "跳过了 1 个不可用项目，没有添加下载项。",
                ContentDownloadNotificationFormatter.Format(new ContentDownloadBatchResult(0, 1)));
        });
    }
}
