using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.Views;
using DownKyi.Views.DownloadManager;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadBatchToolbarLayoutTests
{
    [AvaloniaFact]
    public Task BatchActionsHaveClearSafeAndDestructiveGroupsAcrossThemes()
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
                    AssertToolbar(application, theme);
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
    public Task BatchToolbarUsesDownloadManagerViewportForTemplatedContent()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            application.RequestedThemeVariant = ThemeVariant.Dark;

            var manager = new ViewDownloadManager();
            var downloading = new ViewDownloading();
            var content = Assert.IsType<Grid>(
                downloading.FindControl<Grid>("NameDownloadingContent"));
            var list = Assert.IsType<ListBox>(
                downloading.FindControl<ListBox>("DownloadingList"));
            var toolbar = Assert.IsType<Border>(
                downloading.FindControl<Border>("NameBatchActionBar"));
            content.IsVisible = true;
            var items = new ObservableCollection<DownloadingItem>();
            list.ItemsSource = items;

            var window = new Window
            {
                Content = manager,
                Width = 960,
                Height = 855
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var regionHost = Assert.Single(
                    manager.GetVisualDescendants().OfType<ContentControl>(),
                    candidate => Grid.GetColumn(candidate) == 1 &&
                                 candidate.Bounds.Width > 500 &&
                                 candidate.Bounds.Height > 500);
                regionHost.ContentTemplate = new FuncDataTemplate<object>(
                    (_, _) => downloading);
                regionHost.Content = new object();
                window.UpdateLayout();

                foreach (var item in Enumerable
                             .Range(0, 40)
                             .Select(_ => new DownloadingItem()))
                {
                    items.Add(item);
                }

                window.UpdateLayout();

                Assert.Equal(
                    HorizontalAlignment.Stretch,
                    regionHost.HorizontalContentAlignment);
                Assert.Equal(
                    VerticalAlignment.Stretch,
                    regionHost.VerticalContentAlignment);
                Assert.Equal(regionHost.Bounds.Height, downloading.Bounds.Height);
                Assert.False(double.IsNaN(content.Height));
                Assert.Equal(downloading.Bounds.Height, content.Height);
                Assert.True(
                    BottomInHost(toolbar, regionHost) <= regionHost.Bounds.Height,
                    $"Batch toolbar bottom {BottomInHost(toolbar, regionHost)} exceeds " +
                    $"download-manager viewport {regionHost.Bounds.Height}.");
            }
            finally
            {
                window.Close();
                application.RequestedThemeVariant = originalTheme;
            }
        });
    }

    private static void AssertToolbar(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var view = new ViewDownloading();
        var content = Assert.IsType<Grid>(
            view.FindControl<Grid>("NameDownloadingContent"));
        var toolbar = Assert.IsType<Border>(
            view.FindControl<Border>("NameBatchActionBar"));
        var summary = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameBatchSummary"));
        var count = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDownloadingCount"));
        var safeActions = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameSafeBatchActions"));
        var pause = Assert.IsType<Button>(
            view.FindControl<Button>("NamePauseAllButton"));
        var resume = Assert.IsType<Button>(
            view.FindControl<Button>("NameContinueAllButton"));
        var divider = Assert.IsType<Border>(
            view.FindControl<Border>("NameDeleteBatchDivider"));
        var delete = Assert.IsType<Button>(
            view.FindControl<Button>("NameDeleteAllButton"));
        var downloadList = Assert.IsType<ListBox>(
            view.FindControl<ListBox>("DownloadingList"));
        content.IsVisible = true;
        downloadList.ItemsSource = Enumerable
            .Range(0, 40)
            .Select(_ => new DownloadingItem())
            .ToArray();

        var host = new ContentControl
        {
            Content = view,
            Width = 760,
            Height = 774
        };

        var window = new Window
        {
            Content = host,
            Width = 760,
            Height = 774
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Assert.Equal(58, toolbar.Bounds.Height);
            Assert.Equal(host.Bounds.Height, content.Height);
            Assert.Equal(new Thickness(16, 0), toolbar.Padding);
            Assert.Equal(
                ResourceColor(application, "BrushSurfaceRaised", theme),
                SolidColor(toolbar.Background));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(count.Foreground));
            Assert.NotEqual(
                ResourceColor(application, "BrushPrimary", theme),
                SolidColor(count.Foreground));

            Assert.Same(safeActions, pause.Parent);
            Assert.Same(safeActions, resume.Parent);
            Assert.Equal(104, pause.MinWidth);
            Assert.Equal(104, resume.MinWidth);
            Assert.Equal(104, delete.MinWidth);
            Assert.Equal(HorizontalAlignment.Left, summary.HorizontalAlignment);

            Assert.True(RightInView(summary, view) < LeftInView(pause, view));
            Assert.True(RightInView(pause, view) < LeftInView(resume, view));
            Assert.True(RightInView(resume, view) < LeftInView(divider, view));
            Assert.True(RightInView(divider, view) < LeftInView(delete, view));

            foreach (var button in new[] { pause, resume, delete })
            {
                var chrome = Assert.Single(
                    button.GetVisualDescendants().OfType<Border>());
                var presenter = Assert.Single(
                    button.GetVisualDescendants().OfType<ContentPresenter>());
                var icon = Assert.Single(
                    button.GetVisualDescendants().OfType<ShapePath>());
                Assert.True(button.Bounds.Width >= presenter.Bounds.Width + 24);
                Assert.NotNull(icon.Data);
                Assert.Equal(
                    ResourceColor(application, "BrushControlFill", theme),
                    SolidColor(chrome.Background));
            }

            Assert.Equal(
                ResourceColor(application, "BrushWarning", theme),
                SolidColor(delete.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushWarning", theme),
                SolidColor(Assert.Single(
                    delete.GetVisualDescendants().OfType<ShapePath>()).Fill));
            Assert.True(delete.Bounds.Right <= view.Bounds.Right);
            Assert.True(
                BottomInHost(toolbar, host) <= host.Bounds.Height,
                $"Batch toolbar bottom {BottomInHost(toolbar, host)} exceeds host height {host.Bounds.Height}.");
        }
        finally
        {
            window.Close();
        }
    }

    private static double LeftInView(Control control, ViewDownloading view)
    {
        return control.TranslatePoint(new Point(0, 0), view)!.Value.X;
    }

    private static double RightInView(Control control, ViewDownloading view)
    {
        return LeftInView(control, view) + control.Bounds.Width;
    }

    private static double BottomInHost(Control control, ContentControl host)
    {
        return control.TranslatePoint(new Point(0, control.Bounds.Height), host)!.Value.Y;
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
