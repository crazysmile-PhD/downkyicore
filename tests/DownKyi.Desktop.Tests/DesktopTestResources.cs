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
        try
        {
            application.Resources.MergedDictionaries.Add(new ResourceInclude(
                new Uri("avares://DownKyi.Desktop.Tests/"))
            {
                Source = source
            });
        }
        catch (Exception exception)
        {
            var productAssembly = typeof(DesktopApplication).Assembly;
            var resolvedAssembly = AssetLoader.GetAssembly(source);
            var productResources = string.Join(", ", productAssembly.GetManifestResourceNames());
            var relativeLocation = Path.GetRelativePath(AppContext.BaseDirectory, productAssembly.Location);
            var resolvedIdentity = resolvedAssembly?.FullName ?? "<not resolved>";
            var assetExists = AssetLoader.Exists(source);

            throw new InvalidOperationException(
                $"Unable to load {source}. " +
                $"Product assembly={productAssembly.FullName}; " +
                $"location={relativeLocation}; " +
                $"manifest resources=[{productResources}]; " +
                $"resolved assembly={resolvedIdentity}; " +
                $"asset exists={assetExists}.",
                exception);
        }
    }
}
