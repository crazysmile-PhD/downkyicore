using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;

namespace DownKyi.Desktop.Tests;

internal static class DesktopTestResources
{
    public static Avalonia.Application EnsureDownloadProjectionResources()
    {
        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        if (!application.TryGetResource("Waiting", ThemeVariant.Default, out _))
        {
            AddProductResource(
                application,
                new Uri("avares://DownKyi.Desktop/Languages/Default.axaml"));
        }

        if (!application.TryGetResource("videoUpDrawingImage", ThemeVariant.Default, out _))
        {
            AddProductResource(
                application,
                new Uri("avares://DownKyi.Desktop/Resources/Bilibili/BilibiliImages.axaml"));
        }

        return application;
    }

    public static Avalonia.Application EnsureProductThemeResources()
    {
        var application = Avalonia.Application.Current
            ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        if (!application.TryGetResource("DownKyiRadiusMedium", ThemeVariant.Default, out _))
        {
            AddProductResource(
                application,
                new Uri("avares://DownKyi.Desktop/Themes/DesignTokens.axaml"));
        }

        if (!application.TryGetResource("ImageBtnStyle", ThemeVariant.Default, out _))
        {
            AddProductResource(
                application,
                new Uri("avares://DownKyi.Desktop/Themes/ThemeDefault.axaml"));
        }

        return application;
    }

    private static void AddProductResource(Avalonia.Application application, Uri source)
    {
        var productAssembly = typeof(DesktopApplication).Assembly;
        var resolvedAssembly = AssetLoader.GetAssembly(source);
        if (!ReferenceEquals(resolvedAssembly, productAssembly))
        {
            throw new InvalidOperationException(
                $"Avalonia resource {source} resolved to " +
                $"{resolvedAssembly?.GetName().Name ?? "<no assembly>"} instead of " +
                $"{productAssembly.GetName().Name}.");
        }

        application.Resources.MergedDictionaries.Add(new ResourceInclude(
            new Uri("avares://DownKyi.Desktop.Tests/"))
        {
            Source = source
        });
    }
}
