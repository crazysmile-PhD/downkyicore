using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Views.Toolbox;

namespace DownKyi.Desktop.Tests;

public sealed class ViewExtractMediaVisualStateTests
{
    [AvaloniaFact]
    public Task OutputSurfaceDoesNotChangeWhenPointerMovesAcrossIt()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);
            var view = new ViewExtractMedia();
            var window = new Window
            {
                Content = view,
                Width = 840,
                Height = 620
            };

            try
            {
                window.Show();
                var output = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameStatus"));
                var pointerTarget = output.TranslatePoint(
                    new Point(output.Bounds.Width / 2, output.Bounds.Height / 2),
                    window);
                Assert.NotNull(pointerTarget);

                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.MouseMove(new Point(1, 1));
                    window.UpdateLayout();

                    Assert.True(application.TryGetResource("BrushSurfaceSunken", theme, out var background));
                    var expectedBackground = Assert.IsAssignableFrom<ISolidColorBrush>(background).Color;
                    Assert.True(application.TryGetResource("BrushTextPrimary", theme, out var foreground));
                    var expectedForeground = Assert.IsAssignableFrom<ISolidColorBrush>(foreground).Color;

                    var outputBorder = Assert.Single(
                        output.GetVisualDescendants().OfType<Border>(),
                        border => border.Name == "PART_BorderElement");
                    Assert.Equal(
                        expectedBackground,
                        Assert.IsAssignableFrom<ISolidColorBrush>(outputBorder.Background).Color);

                    window.MouseMove(pointerTarget.Value);
                    window.UpdateLayout();

                    Assert.True(output.IsPointerOver);
                    Assert.Equal(
                        expectedBackground,
                        Assert.IsAssignableFrom<ISolidColorBrush>(outputBorder.Background).Color);
                    Assert.Equal(
                        expectedForeground,
                        Assert.IsAssignableFrom<ISolidColorBrush>(output.Foreground).Color);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                window.Close();
                application.Styles.Remove(fluentTheme);
            }
        });
    }
}
