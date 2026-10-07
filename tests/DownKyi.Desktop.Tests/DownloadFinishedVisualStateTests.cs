using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Core.BiliApi.BiliUtils;
using DownKyi.Models;
using DownKyi.ViewModels.DownloadManager;
using DownKyi.Views.DownloadManager;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DownKyi.Desktop.Tests;

public sealed class DownloadFinishedVisualStateTests
{
    private static readonly ThemeVariant[] Themes =
    [
        ThemeVariant.Light,
        ThemeVariant.Dark
    ];

    [AvaloniaFact]
    public Task FinishedRowsAndToolbarUseClearActionHierarchyAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);

            try
            {
                foreach (var theme in Themes)
                {
                    application.RequestedThemeVariant = theme;
                    AssertFinishedView(application, theme);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertFinishedView(
        Avalonia.Application application,
        ThemeVariant theme)
    {
        var item = new DownloadedItem
        {
            Downloaded = new Downloaded
            {
                FinishedTime = "2026-09-27 15:25:32",
                MaxSpeedDisplay = "2.62 MB/s"
            }
        };
        item.DownloadBase.Order = 1;
        item.DownloadBase.MainTitle = "示例影片：较长标题用于检查已下载任务的可读性";
        item.DownloadBase.Name = "第一集";
        item.DownloadBase.Duration = "12:48";
        item.DownloadBase.Resolution = new Quality { Name = "1080P" };
        item.DownloadBase.VideoCodecName = "AVC";
        item.DownloadBase.AudioCodec = new Quality { Name = "AAC" };
        item.DownloadBase.FileSize = "42.5 MB";

        var view = new ViewDownloadFinished();
        var content = Assert.IsType<Grid>(
            view.FindControl<Grid>("NameDownloadedContent"));
        var list = Assert.IsType<ListBox>(
            view.FindControl<ListBox>("DownloadedList"));
        var toolbar = Assert.IsType<Border>(
            view.FindControl<Border>("NameFinishedActionBar"));
        var summary = Assert.IsType<StackPanel>(
            view.FindControl<StackPanel>("NameFinishedSummary"));
        var count = Assert.IsType<TextBlock>(
            view.FindControl<TextBlock>("NameDownloadedCount"));
        var sort = Assert.IsType<ComboBox>(
            view.FindControl<ComboBox>("NameFinishedSort"));
        var divider = Assert.IsType<Border>(
            view.FindControl<Border>("NameFinishedClearDivider"));
        var clear = Assert.IsType<Button>(
            view.FindControl<Button>("NameClearAllDownloadedButton"));
        content.IsVisible = true;
        list.ItemsSource = new[] { item };
        var selectedSortItem = Assert.IsType<ComboBoxItem>(sort.Items[0]);
        selectedSortItem.Content = "按下载时间升序";
        sort.SelectedIndex = 0;

        var host = new ContentControl
        {
            Content = view,
            Width = 800,
            Height = 500
        };
        var window = new Window
        {
            Content = host,
            Width = 800,
            Height = 500
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var selectedSortText = Assert.IsType<string>(selectedSortItem.Content);
            var measuredSortText = new TextBlock
            {
                FontFamily = sort.FontFamily,
                FontSize = sort.FontSize,
                FontStyle = sort.FontStyle,
                FontWeight = sort.FontWeight,
                Text = selectedSortText
            };
            measuredSortText.Measure(Size.Infinity);
            var selectedSortContent = Assert.Single(
                sort.GetVisualDescendants().OfType<ContentControl>(),
                candidate => candidate.Name == "ContentPresenter");
            Assert.True(
                selectedSortContent.Bounds.Width >= measuredSortText.DesiredSize.Width,
                $"Sort text needs {measuredSortText.DesiredSize.Width} px but only "
                + $"{selectedSortContent.Bounds.Width} px is available.");

            var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
            var row = Named<Border>(container, "NameFinishedTaskRow");
            var iconFrame = Named<Border>(container, "NameFinishedZoneIconFrame");
            var title = Named<TextBlock>(container, "NameFinishedTaskTitle");
            var metadata = Named<TextBlock>(container, "NameFinishedTaskMetadata");
            var finishedTime = Named<TextBlock>(container, "NameFinishedTime");
            var openFolder = Named<Button>(container, "NameFinishedOpenFolderButton");
            var openVideo = Named<Button>(container, "NameFinishedOpenVideoButton");
            var remove = Named<Button>(container, "NameFinishedRemoveButton");

            Assert.Equal(86, row.Bounds.Height);
            Assert.Equal(40, iconFrame.Bounds.Width);
            Assert.Equal(40, iconFrame.Bounds.Height);
            Assert.Equal("2026-09-27 15:25:32", finishedTime.Text);
            Assert.Equal(124, finishedTime.Bounds.Width);
            Assert.Contains("42.5 MB", metadata.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("2.62 MB/s", metadata.Text, StringComparison.Ordinal);
            Assert.True(LeftInView(title, view) < LeftInView(finishedTime, view));
            Assert.True(RightInView(finishedTime, view) < LeftInView(openFolder, view));

            foreach (var button in new[] { openFolder, openVideo, remove })
            {
                Assert.Equal(36, button.Bounds.Width);
                Assert.Equal(36, button.Bounds.Height);
                var chrome = Assert.Single(
                    button.GetVisualDescendants().OfType<Border>());
                Assert.Equal(
                    ResourceColor(application, "BrushControlFill", theme),
                    SolidColor(chrome.Background));
            }

            foreach (var button in new[] { openFolder, openVideo })
            {
                Assert.Equal(
                    ResourceColor(application, "BrushTextPrimary", theme),
                    SolidColor(button.Foreground));
                Assert.NotEqual(
                    ResourceColor(application, "BrushPrimary", theme),
                    SolidColor(button.Foreground));
            }

            Assert.Equal(
                ResourceColor(application, "BrushWarning", theme),
                SolidColor(remove.Foreground));
            Assert.Equal(
                ResourceColor(application, "BrushWarning", theme),
                SolidColor(Named<ShapePath>(remove, null).Fill));
            Assert.Equal(
                ResourceColor(application, "BrushPrimaryTranslucent3", theme),
                SolidColor(iconFrame.Background));

            Assert.Equal(58, toolbar.Bounds.Height);
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
            Assert.Equal(104, clear.MinWidth);
            Assert.Equal(
                ResourceColor(application, "BrushWarning", theme),
                SolidColor(clear.Foreground));
            Assert.True(RightInView(summary, view) < LeftInView(sort, view));
            Assert.True(RightInView(sort, view) < LeftInView(divider, view));
            Assert.True(RightInView(divider, view) < LeftInView(clear, view));
            Assert.True(clear.Bounds.Right <= view.Bounds.Right);
            Assert.True(BottomInHost(toolbar, host) <= host.Bounds.Height);

            sort.IsDropDownOpen = true;
            window.UpdateLayout();
            var selected = Assert.IsType<ComboBoxItem>(sort.ContainerFromIndex(0));
            var selectedPresenter = Assert.Single(
                selected.GetVisualDescendants().OfType<ContentPresenter>(),
                candidate => candidate.Name == "PART_ContentPresenter");
            Assert.True(selected.IsSelected);
            Assert.Equal(
                ResourceColor(application, "BrushSelectionFill", theme),
                SolidColor(selectedPresenter.Background));
            sort.IsDropDownOpen = false;
            window.UpdateLayout();

            var hoverPoint = container.TranslatePoint(new Point(8, 8), window)
                ?? throw new InvalidOperationException(
                    "Could not translate finished-row hover point.");
            window.MouseMove(hoverPoint);
            window.UpdateLayout();
            Assert.True(container.IsPointerOver);
            Assert.Equal(
                ResourceColor(application, "BrushSurfaceRaised", theme),
                SolidColor(row.Background));
        }
        finally
        {
            window.Close();
        }
    }

    private static T Named<T>(Control root, string? name)
        where T : Control
    {
        var controls = root.GetVisualDescendants().OfType<T>();
        return name is null
            ? Assert.Single(controls)
            : Assert.Single(controls, control => control.Name == name);
    }

    private static double LeftInView(Control control, ViewDownloadFinished view)
    {
        return control.TranslatePoint(new Point(0, 0), view)!.Value.X;
    }

    private static double RightInView(Control control, ViewDownloadFinished view)
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
