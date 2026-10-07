using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Views.Settings;

namespace DownKyi.Desktop.Tests;

public sealed class AboutSettingsLayoutTests
{
    [AvaloniaFact]
    public Task FeedbackActionsUseASeparateDiscoverableGroupAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertFeedbackActions(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    [AvaloniaFact]
    public Task DisclaimerUsesReadableMeasureAndSecondaryHierarchyAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    AssertDisclaimerLayout(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertDisclaimerLayout(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new ViewAbout();
        var section = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameDisclaimerSection"));
        var title = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDisclaimerTitle"));
        var itemPanel = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameDisclaimerItems"));
        var items = itemPanel.Children.OfType<TextBlock>().ToArray();
        title.Text = "免责声明";
        for (var index = 0; index < items.Length; index++)
        {
            items[index].Text = $"{index + 1}. {new string('测', 160)}";
        }

        var window = new Window
        {
            Content = view,
            Width = 1500,
            Height = 900
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Equal(880, section.MaxWidth);
            Assert.Equal(HorizontalAlignment.Left, section.HorizontalAlignment);
            Assert.InRange(section.Bounds.Width, 800, 880);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(title.Foreground));
            Assert.Equal(6, items.Length);
            Assert.All(items, item =>
            {
                Assert.Equal(new Thickness(0, 0, 0, 8), item.Margin);
                Assert.Equal(22, item.LineHeight);
                Assert.Equal(TextWrapping.WrapWithOverflow, item.TextWrapping);
                Assert.Equal(
                    ResourceColor(application, "BrushTextSecondary", theme),
                    SolidColor(item.Foreground));
            });
            Assert.Contains(items, item => item.Bounds.Height > item.LineHeight);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertFeedbackActions(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new ViewAbout();
        var versionTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameVersionInformationTitle"));
        var versionActions = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameVersionActions"));
        var feedbackTitle = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameFeedbackAndDiagnosticsTitle"));
        var feedbackTip = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameFeedbackSupportTip"));
        var feedbackActions = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameFeedbackActions"));
        var checkUpdate = Assert.IsType<Button>(
            view.FindControl<Button>("NameCheckUpdateButton"));
        var feedback = Assert.IsType<Button>(
            view.FindControl<Button>("NameFeedbackButton"));
        var openLogs = Assert.IsType<Button>(
            view.FindControl<Button>("NameOpenLogsButton"));
        var exportDiagnosticLog = Assert.IsType<Button>(
            view.FindControl<Button>("NameExportDiagnosticLogButton"));
        versionTitle.Text = "版本信息";
        feedbackTitle.Text = "反馈与诊断";
        feedbackTip.Text = "遇到问题时，可先提交反馈；日志与诊断资料可帮助定位异常。";
        checkUpdate.Content = "检查更新";
        feedback.Content = "反馈问题";
        openLogs.Content = "打开日志";
        exportDiagnosticLog.Content = "导出诊断日志";
        var window = new Window
        {
            Content = view,
            Width = 960,
            Height = 720
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Same(versionActions, checkUpdate.Parent);
            Assert.Same(feedbackActions, feedback.Parent);
            Assert.Same(feedbackActions, openLogs.Parent);
            Assert.Same(feedbackActions, exportDiagnosticLog.Parent);
            Assert.True(BottomInView(versionTitle, view) <= TopInView(versionActions, view));
            Assert.True(BottomInView(versionActions, view) < TopInView(feedbackTitle, view));
            Assert.True(BottomInView(feedbackTitle, view) <= TopInView(feedbackTip, view));
            Assert.True(BottomInView(feedbackTip, view) <= TopInView(feedbackActions, view));

            foreach (var button in new[] { checkUpdate, feedback })
            {
                var chrome = Assert.Single(
                    button.GetVisualDescendants().OfType<Border>());
                var presenter = Assert.Single(
                    button.GetVisualDescendants().OfType<ContentPresenter>());
                Assert.True(double.IsNaN(button.Width));
                Assert.Equal(96, button.MinWidth);
                Assert.True(button.Bounds.Width >= presenter.Bounds.Width + 24);
                Assert.Equal(
                    ResourceColor(application, "BrushControlFill", theme),
                    SolidColor(chrome.Background));
                Assert.NotEqual(
                    ResourceColor(application, "BrushSurfaceBase", theme),
                    SolidColor(chrome.Background));
            }

            foreach (var button in new[] { feedback, openLogs, exportDiagnosticLog })
            {
                Assert.Equal(feedback.Bounds.Y, button.Bounds.Y);
                Assert.True(button.Bounds.Width >= 96);
            }

            Assert.True(feedback.Bounds.Right < openLogs.Bounds.X);
            Assert.True(openLogs.Bounds.Right < exportDiagnosticLog.Bounds.X);
        }
        finally
        {
            window.Close();
        }
    }

    private static double TopInView(Control control, ViewAbout view)
    {
        return control.TranslatePoint(new Point(0, 0), view)!.Value.Y;
    }

    private static double BottomInView(Control control, ViewAbout view)
    {
        return TopInView(control, view) + control.Bounds.Height;
    }

    private static Color ResourceColor(
        Avalonia.Application application,
        string key,
        ThemeVariant theme)
    {
        Assert.True(application.TryGetResource(key, theme, out var value));
        return SolidColor(value);
    }

    private static Color SolidColor(object? value)
    {
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
