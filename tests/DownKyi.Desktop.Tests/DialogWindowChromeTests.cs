using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            new(new ViewAlreadyDownloadedDialog(), HasCloseButton: true, UsesTwoColumns: false),
            new(new ViewDownloadSetter(), HasCloseButton: true, UsesTwoColumns: false),
            new(new ViewParsingSelector(), HasCloseButton: true, UsesTwoColumns: true),
            new(new ViewUpgradingDialog(), HasCloseButton: true, UsesTwoColumns: true),
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

    private sealed record DialogCase(
        Control View,
        bool HasCloseButton,
        bool UsesTwoColumns);
}
