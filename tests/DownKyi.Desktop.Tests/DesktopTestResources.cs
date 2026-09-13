using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace DownKyi.Desktop.Tests;

internal static class DesktopTestResources
{
    public static Avalonia.Application EnsureProductThemeResources()
    {
        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        if (application.TryGetResource("ImageBtnStyle", ThemeVariant.Default, out _))
        {
            return application;
        }

        application.Resources.MergedDictionaries.Add(new ResourceInclude(
            new Uri("avares://DownKyi.Desktop.Tests/"))
        {
            Source = new Uri("avares://DownKyi.Desktop/Themes/ThemeDefault.axaml")
        });
        return application;
    }
}
