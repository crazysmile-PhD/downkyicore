using Avalonia.Styling;
using DownKyi.Core.Settings;
using DownKyi.Desktop.Appearance;

namespace DownKyi.Desktop.Tests;

public sealed class DesktopThemeControllerTests
{
    [Fact]
    public void ThemeModesMapToExplicitAvaloniaVariants()
    {
        Assert.Same(ThemeVariant.Default, DesktopThemeController.ToThemeVariant(ThemeMode.None));
        Assert.Same(ThemeVariant.Default, DesktopThemeController.ToThemeVariant(ThemeMode.Default));
        Assert.Same(ThemeVariant.Light, DesktopThemeController.ToThemeVariant(ThemeMode.Light));
        Assert.Same(ThemeVariant.Dark, DesktopThemeController.ToThemeVariant(ThemeMode.Dark));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopThemeController.ToThemeVariant((ThemeMode)int.MaxValue));
    }

    [AvaloniaFact]
    public async Task LightDarkAndDefaultPersistAndRestoreTheirRequestedVariants()
    {
        await AvaloniaTestDispatcher.RunAsync(async () =>
        {
            var application = DesktopTestResources.EnsureProductThemeResources();
            var originalTheme = application.RequestedThemeVariant;
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"downkyi-theme-controller-{Guid.NewGuid():N}");
            var settingsPath = Path.Combine(directory, "settings.json");

            try
            {
                foreach (var (mode, expectedVariant) in new[]
                         {
                             (ThemeMode.Light, ThemeVariant.Light),
                             (ThemeMode.Dark, ThemeVariant.Dark),
                             (ThemeMode.Default, ThemeVariant.Default)
                         })
                {
                    using (var settings = new SettingsStore(settingsPath))
                    {
                        var controller = new DesktopThemeController(settings);

                        Assert.True(controller.SetMode(mode));
                        Assert.Equal(mode, settings.Current.Basic.ThemeMode);
                        Assert.Same(expectedVariant, application.RequestedThemeVariant);
                        await settings.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                    }

                    application.RequestedThemeVariant = mode == ThemeMode.Dark
                        ? ThemeVariant.Light
                        : ThemeVariant.Dark;

                    using (var restoredSettings = new SettingsStore(settingsPath))
                    {
                        var restoredController = new DesktopThemeController(restoredSettings);

                        Assert.Equal(mode, restoredSettings.Current.Basic.ThemeMode);
                        restoredController.ApplySavedMode();
                        Assert.Same(expectedVariant, application.RequestedThemeVariant);
                    }
                }
            }
            finally
            {
                application.RequestedThemeVariant = originalTheme;
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }).ConfigureAwait(true);
    }
}
