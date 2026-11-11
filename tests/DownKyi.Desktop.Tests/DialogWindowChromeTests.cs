using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DownKyi.Platform;
using DownKyi.Views.Dialogs;

namespace DownKyi.Desktop.Tests;

public sealed class DialogWindowChromeTests
{
    [AvaloniaFact]
    public void DialogWindowUsesBorderlessCustomChrome()
    {
        var window = new DialogWindow();

        Assert.Equal(WindowDecorations.None, window.WindowDecorations);
        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.False(window.CanResize);
        Assert.False(window.CanMinimize);
        Assert.False(window.CanMaximize);
    }

    [AvaloniaFact]
    public void EveryDialogLoadsWithOneCustomTitleBarAndValidCloseLayout()
    {
        DesktopTestResources.EnsureProductThemeResources();
        var cases = new DialogCase[]
        {
            new(new ViewAlertDialog(), HasCloseButton: true, UsesTwoColumns: true),
            new(new ViewDownloadSetter(), HasCloseButton: true, UsesTwoColumns: false),
            new(new ViewUpgradingDialog(), HasCloseButton: true, UsesTwoColumns: true),
            new(new StartupDiagnosticsDialog(), HasCloseButton: true, UsesTwoColumns: true),
            new(new NewVersionAvailableDialog(), HasCloseButton: true, UsesTwoColumns: false)
        };

        foreach (var dialogCase in cases)
        {
            var window = new DialogWindow
            {
                Content = dialogCase.View
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var titleBar = Assert.Single(dialogCase.View
                    .GetVisualDescendants()
                    .OfType<Grid>(),
                    grid => WindowDecorationProperties.GetElementRole(grid) ==
                            WindowDecorationsElementRole.TitleBar);
                var titleButtons = titleBar
                    .GetVisualDescendants()
                    .OfType<Button>()
                    .ToArray();

                if (!dialogCase.HasCloseButton)
                {
                    Assert.Empty(titleButtons);
                    continue;
                }

                var closeButton = Assert.Single(titleButtons);
                Assert.Equal(
                    WindowDecorationsElementRole.User,
                    WindowDecorationProperties.GetElementRole(closeButton));
                Assert.True(closeButton.Bounds.Width > 0);
                Assert.True(closeButton.Bounds.Height > 0);
                var origin = closeButton.TranslatePoint(default, titleBar);
                Assert.NotNull(origin);
                Assert.InRange(origin.Value.X, 0, titleBar.Bounds.Width);
                Assert.InRange(origin.Value.Y, 0, titleBar.Bounds.Height);
                Assert.True(origin.Value.X + closeButton.Bounds.Width <= titleBar.Bounds.Width + 0.5);
                Assert.True(origin.Value.Y + closeButton.Bounds.Height <= titleBar.Bounds.Height + 0.5);

                if (dialogCase.UsesTwoColumns)
                {
                    Assert.Equal(2, titleBar.ColumnDefinitions.Count);
                    Assert.Equal(1, Grid.GetColumn(closeButton));
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void ConfirmationDialogsSeparatePrimaryAndSecondaryActionsAcrossThemes()
    {
        var application = DesktopTestResources.EnsureProductThemeResources();
        var originalTheme = application.RequestedThemeVariant;
        var cases = new (Control View, string PrimaryName, string SecondaryName)[]
        {
            (new ViewAlertDialog(), "ConfirmAlertButton", "CancelAlertButton"),
            (new DownloadContentConflictDialog(), "UseAvailableContentButton", "SkipDownloadPageButton"),
            (new DownloadDuplicateBatchDialog(), "RedownloadDuplicateBatchButton", "SkipDuplicateBatchButton"),
            (new DownloadUnavailableBatchDialog(), "SkipUnavailableBatchButton", "CancelUnavailableBatchButton")
        };

        try
        {
            foreach (var dialogCase in cases)
            {
                var window = new DialogWindow
                {
                    Content = dialogCase.View
                };
                try
                {
                    window.Show();
                    foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    {
                        application.RequestedThemeVariant = theme;
                        window.UpdateLayout();

                        var confirm = Assert.IsType<Button>(
                            dialogCase.View.FindControl<Button>(dialogCase.PrimaryName));
                        var cancel = Assert.IsType<Button>(
                            dialogCase.View.FindControl<Button>(dialogCase.SecondaryName));
                        var confirmChrome = Assert.Single(
                            confirm.GetVisualDescendants().OfType<Border>());
                        var cancelChrome = Assert.Single(
                            cancel.GetVisualDescendants().OfType<Border>());

                        var primary = ResourceColor(application, "SpaceAccent", theme);
                        var onPrimary = ResourceColor(application, "SpaceCarbon", theme);
                        var text = ResourceColor(application, "SpaceInk", theme);
                        var surface = ResourceColor(application, "SpacePaper", theme);

                        Assert.Equal(primary, SolidColor(confirmChrome.Background));
                        Assert.Equal(onPrimary, SolidColor(confirm.Foreground));
                        Assert.True(ContrastRatio(onPrimary, primary) >= 4.5);

                        Assert.Equal(surface, SolidColor(cancelChrome.Background));
                        Assert.Equal(text, SolidColor(cancel.Foreground));
                        Assert.Equal(
                            ResourceColor(application, "SpaceRule", theme),
                            SolidColor(cancelChrome.BorderBrush));
                        Assert.True(ContrastRatio(text, surface) >= 4.5);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            application.RequestedThemeVariant = originalTheme;
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
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linearize(color.R)) +
               (0.7152 * Linearize(color.G)) +
               (0.0722 * Linearize(color.B));
    }

    private sealed record DialogCase(
        Control View,
        bool HasCloseButton,
        bool UsesTwoColumns);
}
