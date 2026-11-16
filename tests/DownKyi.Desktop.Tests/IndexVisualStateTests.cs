using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownKyi.Application.Desktop;
using DownKyi.Application.Settings;
using DownKyi.ViewModels;
using DownKyi.Views;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace DownKyi.Desktop.Tests;

public sealed class IndexVisualStateTests
{
    private static readonly Size[] ResizeSequence =
    [
        new(800, 550), new(960, 700), new(960, 1000), new(800, 1200),
        new(1920, 550), new(1920, 1080), new(960, 834), new(960, 836), new(800, 550)
    ];

    [AvaloniaFact]
    public Task ResizingReflowsThePosterWithoutLosingInputOrLeavingAnEmptyFooter()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            DesktopTestResources.EnsureDownloadProjectionResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);
            var view = new ViewIndex();
            var window = new Window { Content = view, Width = 800, Height = 550 };
            try
            {
                window.Show();
                var input = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameInputUrl"));
                input.Text = "BV1xx411c7mD";
                input.CaretIndex = 4;
                Assert.True(input.Focus(NavigationMethod.Tab));
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    foreach (var size in ResizeSequence)
                    {
                        window.Width = size.Width;
                        window.Height = size.Height;
                        window.UpdateLayout();
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        Assert.Same(input, view.FindControl<TextBox>("NameInputUrl"));
                        Assert.True(input.IsFocused);
                        Assert.Equal("BV1xx411c7mD", input.Text);
                        Assert.Equal(4, input.CaretIndex);
                        AssertResponsiveComposition(view, size);
                    }
                }
            }
            finally
            {
                window.Close();
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void AssertResponsiveComposition(ViewIndex view, Size size)
    {
        var search = BoundsInView(view, "IndexSearch");
        var navigation = BoundsInView(view, "IndexNavigation");
        var artwork = BoundsInView(view, "IndexArtwork");
        var title = BoundsInView(view, "IndexWordmarkLayout");
        Assert.InRange(navigation.Top - search.Bottom, 24, 40);
        Assert.InRange(title.Bottom, 0, search.Top);
        Assert.InRange(title.Left, 0, size.Width);
        Assert.InRange(title.Right, 0, size.Width);
        Assert.InRange(navigation.Bottom, 0, size.Height);
        Assert.InRange(artwork.Bottom, size.Height - 1, size.Height + 1);
        if (size.Height >= size.Width)
        {
            Assert.InRange(artwork.Top - navigation.Bottom, 20, 36);
            Assert.True(title.Height > 200, "Tall windows must use a larger, stacked wordmark.");
            Assert.True(title.Height >= BoundsInView(view, "IndexHero").Height * 0.7,
                $"Tall windows must enlarge the wordmark instead of adding large empty gaps: {size}, title {title}, hero {BoundsInView(view, "IndexHero")}.");
        }
        else if (size.Width >= size.Height * 1.3)
        {
            Assert.InRange(size.Height - navigation.Bottom, 20, 36);
            Assert.True(artwork.Left >= search.Right);
        }
    }

    private static Rect BoundsInView(ViewIndex view, string name)
    {
        var control = Assert.IsAssignableFrom<Control>(view.FindControl<Control>(name));
        var start = control.TranslatePoint(default, view);
        var end = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), view);
        Assert.NotNull(start);
        Assert.NotNull(end);
        return new Rect(start.Value, end.Value);
    }

    [AvaloniaFact]
    public Task EditorialIdentityUsesSharedInkAcrossThemes()
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
                    Assert.IsType<ShapePath>(view.FindControl<ShapePath>("IndexDownloadManagerIcon")),
                    Assert.IsType<ShapePath>(view.FindControl<ShapePath>("IndexToolboxIcon"))
                };
                var settingsIcon = Assert.IsType<ShapePath>(
                    view.FindControl<ShapePath>("IndexSettingsIcon"));

                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    window.UpdateLayout();

                    var brandColor = ResourceColor(application, "SpaceInk", theme);
                    var wordmark = Assert.IsType<TextBlock>(view.FindControl<TextBlock>("IndexWordmark"));
                    Assert.Equal(brandColor, SolidColor(wordmark.Foreground));
                    Assert.Equal(ResourceColor(application, "SpacePaper", theme), SolidColor(view.Background));
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
    public Task SearchInputKeepsOneRectangularFocusSurfaceAcrossThemes()
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
                    Assert.Equal(default, searchBorder.CornerRadius);
                    Assert.Equal(default, innerBorder.CornerRadius);
                    Assert.Equal(new Thickness(1), innerBorder.BorderThickness);
                    Assert.NotNull(innerBorder.Background);
                    Assert.Equal(default, searchBorder.BorderThickness);
                    Assert.Equal(
                        ResourceColor(application, "SpaceAccent", theme),
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

    [AvaloniaFact]
    public Task CompactHomepageKeepsSearchAndNavigationKeyboardAccessible()
    {
        return AvaloniaTestDispatcher.RunAsync(() =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            DesktopTestResources.EnsureDownloadProjectionResources();
            var originalTheme = application.RequestedThemeVariant;
            var fluentTheme = new FluentTheme();
            application.Styles.Insert(0, fluentTheme);
            try
            {
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    application.RequestedThemeVariant = theme;
                    VerifyCompactNavigation(loggedIn: false);
                    VerifyCompactNavigation(loggedIn: true);
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                application.Styles.Remove(fluentTheme);
            }
        });
    }

    private static void VerifyCompactNavigation(bool loggedIn)
    {
        var requests = new List<AppNavigationRequest>();
        var searches = new List<string>();
        var view = new ViewIndex
        {
            DataContext = new StartupIndexViewModel(
                new UserApplicationSettings(42, loggedIn ? "Preview" : string.Empty, loggedIn, false, string.Empty, string.Empty),
                requests.Add,
                searches.Add)
        };
        var window = new Window { Content = view, Width = 800, Height = 550 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var input = Assert.IsType<TextBox>(view.FindControl<TextBox>("NameInputUrl"));
            input.Text = "BV1xx411c7mD";
            Assert.True(input.Focus(NavigationMethod.Tab));
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Assert.Equal("BV1xx411c7mD", Assert.Single(searches));

            var buttons = new[] { "IndexParseButton", "IndexDownloadsButton", "IndexToolboxButton", "IndexSettingsButton", "IndexAccountButton" };
            foreach (var name in buttons)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.UpdateLayout();
                var button = Assert.IsType<Button>(view.FindControl<Button>(name));
                Assert.True(button.IsFocused, $"Tab must reach {name}.");
                var surface = Assert.Single(button.GetVisualDescendants().OfType<Border>(), border => border.Name == "IndexButtonSurface");
                foreach (var text in button.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible))
                {
                    var origin = text.TranslatePoint(default, surface);
                    var end = text.TranslatePoint(new Point(text.Bounds.Width, text.Bounds.Height), surface);
                    Assert.NotNull(origin);
                    Assert.NotNull(end);
                    Assert.InRange(origin.Value.X, 0, surface.Bounds.Width);
                    Assert.InRange(origin.Value.Y, 0, surface.Bounds.Height);
                    Assert.InRange(end.Value.X, 0, surface.Bounds.Width);
                    Assert.InRange(end.Value.Y, 0, surface.Bounds.Height);
                }

                var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window);
                Assert.NotNull(corner);
                Assert.InRange(corner.Value.X, 0, window.Bounds.Width);
                Assert.InRange(corner.Value.Y, 0, window.Bounds.Height);
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            }

            Assert.Equal(2, searches.Count);
            Assert.Equal(
                new[] { AppRoute.DownloadManager, AppRoute.Toolbox, AppRoute.Settings, loggedIn ? AppRoute.MySpace : AppRoute.Login },
                requests.Select(request => request.Route));
        }
        finally
        {
            window.Close();
        }
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
