using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Domain.Downloads;
using DownKyi.Platform;
using DownKyi.Services.Download;
using DownKyi.ViewModels.Dialogs;
using DownKyi.Views.Dialogs;

namespace DownKyi.Desktop.Tests;

public sealed class DialogResponsiveLayoutTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void EveryDialogFitsTheScaledScreenAndItsOwner(double scale)
    {
        foreach (var dialog in Enum.GetValues<AppDialog>())
        {
            var owner = new Size(800, 550);
            var size = AvaloniaDialogService.ConstrainDialogSize(dialog, new PixelRect(0, 0, 1280, 720), scale, owner);
            Assert.InRange(size.Width, 1, Math.Min(1280 / scale - 32, owner.Width - 32));
            Assert.InRange(size.Height, 1, Math.Min(720 / scale - 32, owner.Height - 32));
        }
    }

    [AvaloniaFact]
    public void LongMessagesAndExpandedListsScrollWithoutMovingDecisionButtons()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        DesktopTestResources.EnsureDownloadProjectionResources();
        var originalTheme = application.RequestedThemeVariant;
        var fluent = new FluentTheme();
        application.Styles.Insert(0, fluent);
        var alert = new ViewAlertDialogViewModel
        {
            Title = "确认操作",
            Message = string.Join(Environment.NewLine, Enumerable.Repeat("长提示文字在小视窗仍应可以完整阅读。", 40)),
            TwoButton = true
        };
        var duplicates = new DownloadDuplicateBatchDialogViewModel();
        duplicates.OnDialogOpened(DownloadDuplicateBatchDialogContract.CreateRequest(new DownloadDuplicateBatchPrompt(
            Enumerable.Range(1, 40).Select(i => $"第 {i} 集 · 很长的影片标题用于验证缩小后的自动换行与滚动").ToArray(), 3)));
        var unavailable = new DownloadUnavailableBatchDialogViewModel();
        unavailable.OnDialogOpened(DownloadUnavailableBatchDialogContract.CreateRequest(
            new DownloadUnavailableBatchPrompt(Enumerable.Range(1, 40)
                .Select(i => new DownloadUnavailableBatchPromptItem(
                    $"第 {i} 集 · 无法下载的长影片标题",
                    $"BV{i:D10}",
                    i % 2 == 0
                        ? DownloadUnavailableReason.AccessRestrictedOrUnavailable
                        : DownloadUnavailableReason.Unknown,
                    i % 2 == 0 ? -404 : null))
                .ToArray())));
        var conflict = new DownloadContentConflictDialogViewModel();
        conflict.OnDialogOpened(DownloadContentConflictDialogContract.CreateRequest(
            DownloadContentConflictPrompt.QualitySubstitutionBatch(Enumerable.Range(1, 40).Select(i => new DownloadContentConflictPromptItem(
                $"第 {i} 集 · 长影片名称与原始品质信息",
                new DownloadContentConflict(DownloadContentSelection.All,
                    new DownloadMediaCapabilities(DownloadMediaOutputModes.AudioVideo), DownloadContentSelection.All,
                    new DownloadQualitySubstitutions(new DownloadQualitySubstitution(80, "1080P 高清", 64, "720P 高清"), null)))).ToArray())));
        var cases = new (Control View, BaseDialogViewModel ViewModel, string Confirm)[]
        {
            (new ViewAlertDialog(), alert, "ConfirmAlertButton"),
            (new DownloadDuplicateBatchDialog(), duplicates, "RedownloadDuplicateBatchButton"),
            (new DownloadUnavailableBatchDialog(), unavailable, "SkipUnavailableBatchButton"),
            (new DownloadContentConflictDialog(), conflict, "UseAvailableContentButton")
        };
        try
        {
            foreach (var item in cases)
            {
                item.View.DataContext = item.ViewModel;
                var window = new DialogWindow { Content = item.View, SizeToContent = SizeToContent.Manual, Width = 680, Height = 520 };
                try
                {
                    window.Show();
                    foreach (var expander in item.View.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
                    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    {
                        application.RequestedThemeVariant = theme;
                        foreach (var size in new[] { new Size(360, 260), new Size(480, 360), new Size(680, 520), new Size(960, 800), new Size(360, 260) })
                        {
                            window.Width = size.Width;
                            window.Height = size.Height;
                            window.UpdateLayout();
                            Dispatcher.UIThread.RunJobs();
                            window.UpdateLayout();
                            var scroll = item.View.FindControl<ScrollViewer>("DialogBodyScroll")!;
                            var footer = item.View.FindControl<Border>("DialogFooter")!;
                            scroll.ScrollToEnd();
                            window.UpdateLayout();
                            if (size.Height <= 360)
                            {
                                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                            }
                            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                            var footerTop = footer.TranslatePoint(default, item.View)!.Value.Y;
                            Assert.True(scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), item.View)!.Value.Y <= footerTop);
                            foreach (var button in footer.GetVisualDescendants().OfType<Button>().Where(b => b.IsVisible))
                            {
                                var point = button.TranslatePoint(default, item.View)!.Value;
                                Assert.InRange(point.X, 0, item.View.Bounds.Width - button.Bounds.Width + 0.5);
                                Assert.InRange(point.Y, 0, item.View.Bounds.Height - button.Bounds.Height + 0.5);
                                Assert.True(button.Bounds.Height >= 40);
                            }
                        }
                    }
                    AppDialogResult? result = null;
                    item.ViewModel.CloseRequested += (_, value) => result = value;
                    var confirm = item.View.FindControl<Button>(item.Confirm)!;
                    var click = confirm.TranslatePoint(new Point(confirm.Bounds.Width / 2, confirm.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(click, MouseButton.Left);
                    window.MouseUp(click, MouseButton.Left);
                    Assert.NotNull(result);
                    Assert.Equal(AppDialogOutcome.Accepted, result.Outcome);
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            application.RequestedThemeVariant = originalTheme;
            application.Styles.Remove(fluent);
        }
    }
}
