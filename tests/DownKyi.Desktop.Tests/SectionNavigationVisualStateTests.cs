using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DownKyi.Images;
using DownKyi.Presentation;
using DownKyi.Views;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DownKyi.Desktop.Tests;

public sealed class SectionNavigationVisualStateTests
{
    [AvaloniaFact]
    public Task SectionMenusShareOneRoundedSelectedStateAcrossThemes()
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
                    var listTheme = GetTheme(application, "LeftTabHeaderStyle", theme);
                    var itemTheme = GetTheme(application, "LeftTabHeaderItemStyle", theme);

                    AssertTargetViewUsesSharedThemes(new ViewToolbox(), listTheme, itemTheme);
                    AssertTargetViewUsesSharedThemes(new ViewDownloadManager(), listTheme, itemTheme);
                    AssertTargetViewUsesSharedThemes(new ViewSettings(), listTheme, itemTheme);
                    AssertSelectedState(application, theme, listTheme, itemTheme);
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
    public Task SectionMenuIconsUseSemanticThemeColorsAcrossStates()
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
                    AssertIconStates(
                        application,
                        theme,
                        GetTheme(application, "LeftTabHeaderStyle", theme),
                        GetTheme(application, "LeftTabHeaderItemStyle", theme));
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
    public Task SectionBackHeadersKeepTextInsideTheirHoverSurfaceAcrossThemes()
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

                    AssertBackHeaderContainsTitle(new ViewSettings());
                    AssertBackHeaderContainsTitle(new ViewDownloadManager());
                    AssertBackHeaderContainsTitle(new ViewToolbox());
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertTargetViewUsesSharedThemes(
        UserControl view,
        ControlTheme listTheme,
        ControlTheme itemTheme)
    {
        var navigation = Assert.IsType<ListBox>(view.FindControl<ListBox>("NameLeftTabHeaders"));
        Assert.Same(listTheme, navigation.Theme);
        Assert.Same(itemTheme, navigation.ItemContainerTheme);
    }

    private static void AssertBackHeaderContainsTitle(UserControl view)
    {
        var window = new Window
        {
            Content = view,
            Width = 600,
            Height = 240
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var button = Assert.Single(
                view.GetVisualDescendants().OfType<Button>());
            var chrome = Assert.Single(
                button.GetVisualDescendants().OfType<Border>());
            var title = Assert.Single(
                button.GetVisualDescendants().OfType<TextBlock>());
            var titleRight = title.TranslatePoint(
                new Point(title.Bounds.Width, 0),
                chrome);

            Assert.NotNull(titleRight);
            Assert.True(
                chrome.Bounds.Width - titleRight.Value.X >= 8,
                $"{view.GetType().Name} leaves less than 8 px after its title.");
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertSelectedState(
        Avalonia.Application application,
        ThemeVariant theme,
        ControlTheme listTheme,
        ControlTheme itemTheme)
    {
        var navigation = new ListBox
        {
            Width = 200,
            Height = 160,
            Theme = listTheme,
            ItemContainerTheme = itemTheme,
            ItemsSource = new[]
            {
                new TabHeader { Id = 0, Title = "First" },
                new TabHeader { Id = 1, Title = "Second" }
            },
            SelectedIndex = 1
        };
        var window = new Window
        {
            Content = navigation,
            Width = 300,
            Height = 240
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var items = navigation
                .GetVisualDescendants()
                .OfType<ListBoxItem>()
                .ToArray();
            Assert.Equal(2, items.Length);
            var unselected = Assert.Single(items, item => !item.IsSelected);
            var selected = Assert.Single(items, item => item.IsSelected);
            var unselectedBackground = FindTemplateBorder(unselected, "SelectionBackground");
            var selectedBackground = FindTemplateBorder(selected, "SelectionBackground");

            Assert.Equal(new Thickness(12, 2), selected.Margin);
            Assert.Equal(40, selected.MinHeight);
            Assert.Equal(new CornerRadius(10), selectedBackground.CornerRadius);
            Assert.Equal(Colors.Transparent, SolidColor(unselectedBackground.Background));
            Assert.Equal(
                ResourceColor(application, "BrushSelectionFill", theme),
                SolidColor(selectedBackground.Background));
            Assert.DoesNotContain(
                selected.GetVisualDescendants().OfType<Border>(),
                border => border.Name == "SelectionIndicator");

            var clickPoint = unselected.TranslatePoint(
                new Point(unselected.Bounds.Width - 4, unselected.Bounds.Height / 2),
                window);
            Assert.NotNull(clickPoint);
            window.MouseMove(clickPoint.Value);
            window.MouseDown(clickPoint.Value, MouseButton.Left);
            window.MouseUp(clickPoint.Value, MouseButton.Left);
            window.UpdateLayout();
            Assert.Equal(0, navigation.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertIconStates(
        Avalonia.Application application,
        ThemeVariant theme,
        ControlTheme listTheme,
        ControlTheme itemTheme)
    {
        var navigation = new ListBox
        {
            Width = 200,
            Height = 160,
            Theme = listTheme,
            ItemContainerTheme = itemTheme,
            ItemsSource = new[]
            {
                new TabHeader
                {
                    Id = 0,
                    Image = NormalIcon.Current.Downloading,
                    Title = "Downloading"
                },
                new TabHeader
                {
                    Id = 1,
                    Image = NormalIcon.Current.DownloadFinished,
                    Title = "Downloaded"
                }
            },
            SelectedIndex = 1
        };
        var window = new Window
        {
            Content = navigation,
            Width = 300,
            Height = 240
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            var items = navigation
                .GetVisualDescendants()
                .OfType<ListBoxItem>()
                .ToArray();
            var unselected = Assert.Single(items, item => !item.IsSelected);
            var selected = Assert.Single(items, item => item.IsSelected);
            var unselectedIcon = FindTemplatePath(unselected, "image");
            var selectedIcon = FindTemplatePath(selected, "image");
            var surface = ResourceColor(application, "BrushSurfaceBase", theme);
            var selectedSurface = Composite(
                ResourceColor(application, "BrushSelectionFill", theme),
                surface);

            Assert.Equal(
                ResourceColor(application, "BrushTextSecondary", theme),
                SolidColor(unselectedIcon.Fill));
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(selectedIcon.Fill));
            Assert.True(ContrastRatio(SolidColor(unselectedIcon.Fill), surface) >= 3);
            Assert.True(ContrastRatio(SolidColor(selectedIcon.Fill), selectedSurface) >= 3);

            var hoverPoint = unselected.TranslatePoint(
                new Point(unselected.Bounds.Width / 2, unselected.Bounds.Height / 2),
                window);
            Assert.NotNull(hoverPoint);
            window.MouseMove(hoverPoint.Value);
            window.UpdateLayout();

            var hoverSurface = Composite(
                ResourceColor(application, "BrushControlFillHover", theme),
                surface);
            Assert.Equal(
                ResourceColor(application, "BrushTextPrimary", theme),
                SolidColor(unselectedIcon.Fill));
            Assert.True(ContrastRatio(SolidColor(unselectedIcon.Fill), hoverSurface) >= 3);
        }
        finally
        {
            window.Close();
        }
    }

    private static ControlTheme GetTheme(
        Avalonia.Application application,
        string key,
        ThemeVariant theme)
    {
        Assert.True(application.TryGetResource(key, theme, out var value));
        return Assert.IsType<ControlTheme>(value);
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

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            Blend(foreground.R, background.R, alpha),
            Blend(foreground.G, background.G, alpha),
            Blend(foreground.B, background.B, alpha));
    }

    private static byte Blend(byte foreground, byte background, double alpha)
    {
        return (byte)Math.Round((foreground * alpha) + (background * (1 - alpha)));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        return (0.2126 * Linearize(color.R))
            + (0.7152 * Linearize(color.G))
            + (0.0722 * Linearize(color.B));
    }

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Border FindTemplateBorder(Control item, string name)
    {
        return Assert.Single(
            item.GetVisualDescendants().OfType<Border>(),
            border => border.Name == name);
    }

    private static ShapePath FindTemplatePath(Control item, string name)
    {
        return Assert.Single(
            item.GetVisualDescendants().OfType<ShapePath>(),
            path => path.Name == name);
    }
}
