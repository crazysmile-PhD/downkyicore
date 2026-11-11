using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadUnavailableBatchDialogPresentationTests
{
    [AvaloniaFact]
    public Task EveryFailedVideoShowsItsApproximateReasonAndTheTwoBatchChoices()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            DesktopTestResources.EnsureProductThemeResources();
            var viewModel = new DownloadUnavailableBatchDialogViewModel();
            viewModel.OnDialogOpened(DownloadUnavailableBatchDialogContract.CreateRequest(
                CreatePrompt()));
            var view = new DownloadUnavailableBatchDialog
            {
                DataContext = viewModel
            };
            var window = new DialogWindow
            {
                SizeToContent = SizeToContent.Manual,
                Width = 720,
                Height = 600,
                Content = view
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                Assert.Equal("部分影片无法准备", viewModel.Title);
                Assert.Equal("以下 4 部影片将被跳过", viewModel.AffectedItemsHeader);
                Assert.Equal("跳过这 4 部并继续", viewModel.SkipText);
                Assert.Equal("取消整个批次", viewModel.CancelText);
                Assert.Equal(
                    [
                        "账号未登录、登录已失效，或此内容需要登录后访问。",
                        "当前账号可能没有访问权限，或者影片已经不可见。",
                        "影片已失效、不可见或无法取得稿件资料。",
                        "无法确定失败原因。详细信息已写入日志。"
                    ],
                    viewModel.Items.Select(item => item.Reason).ToArray());
                Assert.Equal(
                    ["B 站错误代码：-101", "B 站错误代码：-404", "B 站错误代码：62002", string.Empty],
                    viewModel.Items.Select(item => item.TechnicalDetail).ToArray());

                var items = Assert.IsType<ListBox>(
                    view.FindControl<ListBox>("UnavailableItemsList"));
                var itemsScroll = Assert.Single(
                    items.GetVisualDescendants().OfType<ScrollViewer>());
                Assert.Same(viewModel.Items, items.ItemsSource);
                Assert.Equal(300, items.MaxHeight);
                Assert.Equal(ScrollBarVisibility.Auto, itemsScroll.VerticalScrollBarVisibility);
                Assert.Single(items.GetVisualDescendants().OfType<VirtualizingStackPanel>());
                Assert.True(view.FindControl<Button>("SkipUnavailableBatchButton")!.IsVisible);
                Assert.True(view.FindControl<Button>("CancelUnavailableBatchButton")!.IsVisible);
                Assert.All(
                    view.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => !string.IsNullOrEmpty(text.Text)),
                    text => Assert.True(text.Bounds.Width <= 720));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [AvaloniaFact]
    public Task LargeFailureBatchOnlyRealizesVisibleRows()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            DesktopTestResources.EnsureDownloadProjectionResources();
            DesktopTestResources.EnsureProductThemeResources();
            var viewModel = new DownloadUnavailableBatchDialogViewModel();
            viewModel.OnDialogOpened(DownloadUnavailableBatchDialogContract.CreateRequest(
                new DownloadUnavailableBatchPrompt(
                    Enumerable.Range(1, 500)
                        .Select(index => new DownloadUnavailableBatchPromptItem(
                            $"Unavailable video {index}",
                            $"BV{index:D10}",
                            DownloadUnavailableReason.Unknown))
                        .ToArray())));
            var view = new DownloadUnavailableBatchDialog
            {
                DataContext = viewModel
            };
            var window = new DialogWindow
            {
                SizeToContent = SizeToContent.Manual,
                Width = 720,
                Height = 600,
                Content = view
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var items = Assert.IsType<ListBox>(
                    view.FindControl<ListBox>("UnavailableItemsList"));
                var realizedCount = items.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Count();
                Assert.InRange(realizedCount, 1, 499);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static DownloadUnavailableBatchPrompt CreatePrompt() => new(
    [
        new DownloadUnavailableBatchPromptItem(
            "需要登录才能访问的影片",
            "BV1Q541167Qg",
            DownloadUnavailableReason.AccountRequired,
            -101),
        new DownloadUnavailableBatchPromptItem(
            "没有试看内容的充电影片",
            "BV1ADgC6sEUj",
            DownloadUnavailableReason.AccessRestrictedOrUnavailable,
            -404),
        new DownloadUnavailableBatchPromptItem(
            "已经不可见的稿件",
            "BV1xx411c7mD",
            DownloadUnavailableReason.VideoUnavailable,
            62002),
        new DownloadUnavailableBatchPromptItem(
            "无法识别的失败",
            "BV17x411w7KC",
            DownloadUnavailableReason.Unknown)
    ]);
}
