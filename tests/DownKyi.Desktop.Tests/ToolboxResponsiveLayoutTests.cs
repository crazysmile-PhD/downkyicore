using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Views.Toolbox;

namespace DownKyi.Desktop.Tests;

public sealed class ToolboxResponsiveLayoutTests
{
    [AvaloniaFact]
    public Task PrimaryActionsStayVisibleAtToolboxContentWidth()
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
                    AssertBiliHelperTasksHaveClearFlow();
                    AssertExtractMediaActionIsVisible();
                    AssertDelogoControlsDoNotOverlap();
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertBiliHelperTasksHaveClearFlow()
    {
        var view = new ViewBiliHelper();
        var window = CreateToolboxContentWindow(view);

        try
        {
            window.Show();
            window.UpdateLayout();

            var avid = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameAvid"));
            var bvid = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameBvid"));
            var gotoWeb = Assert.IsType<Button>(view.FindControl<Button>("NameGotoWebButton"));
            var userId = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameUserId"));
            var findSender = Assert.IsType<Button>(
                view.FindControl<Button>("NameFindDanmakuSenderButton"));
            var userSpace = Assert.IsType<Button>(
                view.FindControl<Button>("NameUserSpaceButton"));
            var avidOrigin = Assert.NotNull(avid.TranslatePoint(default, window));
            var bvidOrigin = Assert.NotNull(bvid.TranslatePoint(default, window));
            var gotoWebOrigin = Assert.NotNull(gotoWeb.TranslatePoint(default, window));
            var userIdOrigin = Assert.NotNull(userId.TranslatePoint(default, window));
            var findSenderOrigin = Assert.NotNull(findSender.TranslatePoint(default, window));
            var userSpaceOrigin = Assert.NotNull(userSpace.TranslatePoint(default, window));

            Assert.Equal(avidOrigin.Y, bvidOrigin.Y, 0.5);
            Assert.Equal(avid.Bounds.Width, bvid.Bounds.Width, 0.5);
            Assert.True(avidOrigin.X + avid.Bounds.Width < bvidOrigin.X);
            Assert.InRange(gotoWebOrigin.X - bvidOrigin.X - bvid.Bounds.Width, 8, 16);
            Assert.True(gotoWebOrigin.X + gotoWeb.Bounds.Width <= window.ClientSize.Width);

            Assert.Equal(userIdOrigin.Y, findSenderOrigin.Y, 0.5);
            Assert.Equal(userIdOrigin.Y, userSpaceOrigin.Y, 0.5);
            Assert.InRange(findSenderOrigin.X - userIdOrigin.X - userId.Bounds.Width, 8, 16);
            Assert.InRange(userSpaceOrigin.X - findSenderOrigin.X - findSender.Bounds.Width, 8, 16);
            Assert.True(userSpace.Bounds.Width >= 160);
            Assert.True(userSpaceOrigin.X + userSpace.Bounds.Width <= window.ClientSize.Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertExtractMediaActionIsVisible()
    {
        var view = new ViewExtractMedia();
        var window = CreateToolboxContentWindow(view);

        try
        {
            window.Show();
            window.UpdateLayout();

            var selectButton = Assert.IsType<Button>(
                view.FindControl<Button>("NameSelectVideoButton"));
            var workflow = Assert.IsType<Border>(
                view.FindControl<Border>("NameExtractionWorkflow"));
            var videoPaths = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameVideoPaths"));
            var audioButton = Assert.IsType<Button>(
                view.FindControl<Button>("NameExtractAudioButton"));
            var videoButton = Assert.IsType<Button>(
                view.FindControl<Button>("NameExtractVideoButton"));
            var output = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameStatus"));
            var workflowOrigin = Assert.NotNull(workflow.TranslatePoint(default, window));
            var selectOrigin = Assert.NotNull(selectButton.TranslatePoint(default, window));
            var pathsOrigin = Assert.NotNull(videoPaths.TranslatePoint(default, window));
            var audioOrigin = Assert.NotNull(audioButton.TranslatePoint(default, window));
            var videoOrigin = Assert.NotNull(videoButton.TranslatePoint(default, window));
            var outputOrigin = Assert.NotNull(output.TranslatePoint(default, window));
            var workflowColor = SolidColor(workflow.Background);
            var idleColor = CurrentResourceColor("BrushControlFill");
            var hoverColor = CurrentResourceColor("BrushControlFillHover");
            var pressedColor = CurrentResourceColor("BrushControlFillPressed");

            Assert.True(workflowOrigin.X >= 0);
            Assert.True(workflowOrigin.X + workflow.Bounds.Width <= window.ClientSize.Width);
            Assert.True(selectOrigin.X >= pathsOrigin.X + videoPaths.Bounds.Width);
            Assert.InRange(selectOrigin.X - pathsOrigin.X - videoPaths.Bounds.Width, 8, 16);
            Assert.Equal(pathsOrigin.Y + (videoPaths.Bounds.Height - selectButton.Bounds.Height) / 2, selectOrigin.Y, 0.5);
            Assert.True(selectOrigin.X + selectButton.Bounds.Width <= window.ClientSize.Width);
            Assert.Equal(audioOrigin.Y, videoOrigin.Y, 0.5);
            Assert.Equal(audioButton.Bounds.Width, videoButton.Bounds.Width, 0.5);
            Assert.InRange(videoOrigin.X - audioOrigin.X - audioButton.Bounds.Width, 8, 16);
            Assert.Equal(pathsOrigin.X, audioOrigin.X, 0.5);
            Assert.Equal(
                selectOrigin.X + selectButton.Bounds.Width,
                videoOrigin.X + videoButton.Bounds.Width,
                0.5);
            Assert.True(audioOrigin.Y > pathsOrigin.Y + videoPaths.Bounds.Height);
            Assert.True(audioButton.Bounds.Width >= 250);
            Assert.True(outputOrigin.Y > workflowOrigin.Y + workflow.Bounds.Height);
            Assert.InRange(output.Bounds.Height, 180, 220);
            Assert.True(output.Bounds.Height < window.ClientSize.Height / 2);

            foreach (var button in new[] { selectButton, audioButton, videoButton })
            {
                var chrome = Assert.Single(button.GetVisualDescendants().OfType<Border>());
                Assert.Equal(idleColor, SolidColor(chrome.Background));
                Assert.NotEqual(workflowColor, SolidColor(chrome.Background));
            }

            Assert.NotEqual(idleColor, hoverColor);
            Assert.NotEqual(hoverColor, pressedColor);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertDelogoControlsDoNotOverlap()
    {
        var view = new ViewDelogo();
        var window = CreateToolboxContentWindow(view);

        try
        {
            window.Show();
            window.UpdateLayout();

            var videoPath = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameVideoPath"));
            var selectVideo = Assert.IsType<Button>(
                view.FindControl<Button>("NameSelectVideoButton"));
            var preview = Assert.IsType<Border>(view.FindControl<Border>("NameVideoPreview"));
            var geometryPanel = Assert.IsType<Border>(
                view.FindControl<Border>("NameLogoGeometryPanel"));
            var geometryTitle = Assert.IsType<TextBlock>(
                view.FindControl<TextBlock>("NameLogoGeometryTitle"));
            var geometryTip = Assert.IsType<TextBlock>(
                view.FindControl<TextBlock>("NameLogoGeometryTip"));
            var widthInput = Assert.IsType<TextBox>(
                view.FindControl<TextBox>("NameLogoWidth"));
            var heightInput = Assert.IsType<TextBox>(
                view.FindControl<TextBox>("NameLogoHeight"));
            var xInput = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameLogoX"));
            var yInput = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameLogoY"));
            var color = Assert.IsType<ComboBox>(view.FindControl<ComboBox>("NameBorderColor"));
            var delogoButton = Assert.IsType<Button>(view.FindControl<Button>("NameDelogoButton"));
            var output = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameStatus"));
            selectVideo.Command = new TestCommand();
            delogoButton.Command = new TestCommand();
            window.UpdateLayout();
            var pathOrigin = Assert.NotNull(videoPath.TranslatePoint(default, window));
            var selectOrigin = Assert.NotNull(selectVideo.TranslatePoint(default, window));
            var previewOrigin = Assert.NotNull(preview.TranslatePoint(default, window));
            var geometryPanelOrigin = Assert.NotNull(geometryPanel.TranslatePoint(default, window));
            var geometryTitleOrigin = Assert.NotNull(geometryTitle.TranslatePoint(default, window));
            var geometryTipOrigin = Assert.NotNull(geometryTip.TranslatePoint(default, window));
            var widthOrigin = Assert.NotNull(widthInput.TranslatePoint(default, window));
            var heightOrigin = Assert.NotNull(heightInput.TranslatePoint(default, window));
            var xOrigin = Assert.NotNull(xInput.TranslatePoint(default, window));
            var yOrigin = Assert.NotNull(yInput.TranslatePoint(default, window));
            var colorOrigin = Assert.NotNull(color.TranslatePoint(default, window));
            var buttonOrigin = Assert.NotNull(delogoButton.TranslatePoint(default, window));
            var outputOrigin = Assert.NotNull(output.TranslatePoint(default, window));

            Assert.Equal(pathOrigin.Y, selectOrigin.Y, 0.5);
            Assert.InRange(selectOrigin.X - pathOrigin.X - videoPath.Bounds.Width, 8, 16);
            Assert.True(selectOrigin.X + selectVideo.Bounds.Width <= window.ClientSize.Width);

            Assert.Equal(widthOrigin.Y, heightOrigin.Y, 0.5);
            Assert.Equal(widthInput.Bounds.Width, heightInput.Bounds.Width, 0.5);
            Assert.Equal(xOrigin.Y, yOrigin.Y, 0.5);
            Assert.Equal(xInput.Bounds.Width, yInput.Bounds.Width, 0.5);
            Assert.InRange(geometryPanel.Bounds.Width, 280, 300);
            Assert.Equal(previewOrigin.Y, geometryPanelOrigin.Y, 0.5);
            Assert.Equal(preview.Bounds.Height, geometryPanel.Bounds.Height, 0.5);
            Assert.True(preview.Bounds.Width >= 360);
            Assert.Equal(Color.FromRgb(24, 24, 24), SolidColor(preview.Background));
            Assert.True(widthInput.Bounds.Width >= 120);
            Assert.True(heightInput.Bounds.Width >= 120);
            Assert.True(xInput.Bounds.Width >= 120);
            Assert.True(yInput.Bounds.Width >= 120);
            Assert.Equal(
                CurrentResourceColor("BrushSurfaceRaised"),
                SolidColor(geometryPanel.Background));
            Assert.NotEqual(
                CurrentResourceColor("BrushSurfaceBase"),
                SolidColor(geometryPanel.Background));
            Assert.Equal(
                CurrentResourceColor("BrushBorderSubtle"),
                SolidColor(geometryPanel.BorderBrush));
            var idleColor = CurrentResourceColor("BrushControlFill");
            var selectVideoChrome = Assert.Single(
                selectVideo.GetVisualDescendants().OfType<Border>());
            var delogoButtonChrome = Assert.Single(
                delogoButton.GetVisualDescendants().OfType<Border>());
            Assert.Equal(idleColor, SolidColor(selectVideoChrome.Background));
            Assert.Equal(idleColor, SolidColor(delogoButtonChrome.Background));
            Assert.NotEqual(
                CurrentResourceColor("BrushSurfaceBase"),
                SolidColor(selectVideoChrome.Background));
            Assert.NotEqual(
                CurrentResourceColor("BrushSurfaceRaised"),
                SolidColor(delogoButtonChrome.Background));
            Assert.True(geometryTipOrigin.Y > geometryTitleOrigin.Y + geometryTitle.Bounds.Height);
            Assert.True(widthOrigin.Y > geometryTipOrigin.Y + geometryTip.Bounds.Height);
            Assert.InRange(geometryTip.Bounds.Height, 12, 24);
            Assert.Equal(CurrentResourceColor("BrushTextPrimary"), SolidColor(geometryTitle.Foreground));
            Assert.Equal(CurrentResourceColor("BrushTextSecondary"), SolidColor(geometryTip.Foreground));
            Assert.True(xOrigin.Y > widthOrigin.Y + widthInput.Bounds.Height);
            Assert.True(widthOrigin.X >= previewOrigin.X + preview.Bounds.Width + 16);
            Assert.True(colorOrigin.Y > yOrigin.Y + yInput.Bounds.Height);
            Assert.True(buttonOrigin.Y > colorOrigin.Y + color.Bounds.Height);
            Assert.True(buttonOrigin.X + delogoButton.Bounds.Width <= window.ClientSize.Width);
            Assert.InRange(preview.Bounds.Height, 320, 332);
            Assert.True(outputOrigin.Y > previewOrigin.Y + preview.Bounds.Height);
            Assert.InRange(output.Bounds.Height, 120, 160);
            Assert.True(outputOrigin.X + output.Bounds.Width <= window.ClientSize.Width);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window CreateToolboxContentWindow(UserControl view) => new()
    {
        Content = view,
        Width = 800,
        Height = 630,
        CanResize = false
    };

    private static Color CurrentResourceColor(string key)
    {
        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        var theme = application.RequestedThemeVariant ?? ThemeVariant.Default;
        Assert.True(application.TryGetResource(key, theme, out var value));
        return SolidColor(value);
    }

    private static Color SolidColor(object? value)
    {
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }

    private sealed class TestCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
