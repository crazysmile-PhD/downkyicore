using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Views;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DownKyi.Desktop.Tests;

public sealed class ViewIndexVisualStateTests
{
    [AvaloniaFact]
    public Task BrandIdentityUsesAppIconBlueAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var view = new ViewIndex();
            var window = new Window
            {
                Content = view,
                Width = 840,
                Height = 620
            };

            try
            {
                window.Show();
                var brandFillMarks = new[]
                {
                    Assert.IsType<ShapePath>(view.FindControl<ShapePath>("IndexWordmark")),
                    Assert.IsType<ShapePath>(view.FindControl<ShapePath>("IndexDownloadManagerIcon")),
                    Assert.IsType<ShapePath>(view.FindControl<ShapePath>("IndexToolboxIcon"))
                };
                var settingsIcon = Assert.IsType<ShapePath>(
                    view.FindControl<ShapePath>("IndexSettingsIcon"));

                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.UpdateLayout();

                    var brandColor = ResourceColor(application, "BrushBrand", theme);
                    Assert.Equal(Color.FromArgb(0xFF, 0x77, 0xCB, 0xE0), brandColor);
                    Assert.All(brandFillMarks, mark => Assert.Equal(brandColor, SolidColor(mark.Fill)));
                    Assert.Equal(Colors.Transparent, SolidColor(settingsIcon.Fill));
                    Assert.Equal(brandColor, SolidColor(settingsIcon.Stroke));
                    Assert.Equal(1.75, settingsIcon.StrokeThickness);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                window.Close();
            }
        });
    }

    [AvaloniaFact]
    public Task SearchInputKeepsOneRoundedFocusSurfaceAcrossThemes()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);
            var view = new ViewIndex();
            var window = new Window
            {
                Content = view,
                Width = 840,
                Height = 620
            };

            try
            {
                window.Show();
                var searchBorder = Assert.IsType<Border>(
                    view.FindControl<Border>("IndexSearchBorder"));
                var input = Assert.IsType<TextBox>(
                    view.FindControl<TextBox>("NameInputUrl"));

                Assert.Equal(default, searchBorder.BorderThickness);
                Assert.True(input.Focus());
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.UpdateLayout();

                    var innerBorder = Assert.Single(
                        input.GetVisualDescendants().OfType<Border>(),
                        border => border.Name == "PART_BorderElement");
                    Assert.True(searchBorder.IsKeyboardFocusWithin);
                    Assert.Equal(new CornerRadius(20), searchBorder.CornerRadius);
                    Assert.Equal(new CornerRadius(20), innerBorder.CornerRadius);
                    Assert.Equal(new Thickness(1), innerBorder.BorderThickness);
                    Assert.NotNull(innerBorder.Background);
                    Assert.Equal(default, searchBorder.BorderThickness);
                    Assert.Equal(
                        ResourceColor(application, "BrushControlStrokeFocus", theme),
                        SolidColor(innerBorder.BorderBrush));
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
