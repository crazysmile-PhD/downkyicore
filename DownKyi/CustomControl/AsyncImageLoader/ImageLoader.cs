using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;

namespace DownKyi.CustomControl.AsyncImageLoader;

internal static class ImageLoader
{
    private static readonly ParametrizedLogger? Logger =
        Avalonia.Logging.Logger.TryGet(LogEventLevel.Error, AsyncImageLoaderLogArea);

    public const string AsyncImageLoaderLogArea = "AsyncImageLoader";

    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Source", typeof(ImageLoader));

    public static readonly AttachedProperty<bool> IsLoadingProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("IsLoading", typeof(ImageLoader));

    private static readonly AttachedProperty<CancellationTokenSource?> PendingOperationProperty =
        AvaloniaProperty.RegisterAttached<Image, CancellationTokenSource?>(
            "PendingOperation",
            typeof(ImageLoader));

    public static IAsyncImageLoader AsyncImageLoader { get; set; } = NullAsyncImageLoader.Instance;

    static ImageLoader()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
    }

    private static void OnSourceChanged(Image sender, AvaloniaPropertyChangedEventArgs args)
    {
        _ = OnSourceChangedAsync(sender, args);
    }

    private static async Task OnSourceChangedAsync(Image sender, AvaloniaPropertyChangedEventArgs args)
    {
        var url = args.GetNewValue<string?>();

        var cts = ReplacePendingOperation(sender);

        if (url == null)
        {
            if (CompletePendingOperation(sender, cts))
            {
                SetIsLoading(sender, false);
            }

            sender.Source = null;
            return;
        }

        SetIsLoading(sender, true);
        try
        {
            var desiredSize = sender.DesiredSize;
            var scale = (TopLevel.GetTopLevel(sender) as Window)?.DesktopScaling ?? 1d;
            var targetSize = desiredSize.Width > 0 && desiredSize.Height > 0
                ? new PixelSize(
                    Convert.ToInt32(desiredSize.Width * scale),
                    Convert.ToInt32(desiredSize.Height * scale))
                : (PixelSize?)null;

            var bitmap = await Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(10, cts.Token).ConfigureAwait(false);
                    var loaded = await AsyncImageLoader.ProvideImageAsync(url).ConfigureAwait(false);
                    if (loaded == null || !targetSize.HasValue)
                    {
                        return loaded;
                    }

                    using (loaded)
                    {
                        return loaded.CreateScaledBitmap(targetSize.Value);
                    }
                }
                catch (TaskCanceledException)
                {
                    return null;
                }
                catch (HttpRequestException e)
                {
                    Logger?.Log(LogEventLevel.Error, "ImageLoader image resolution failed: {0}", e);
                    return null;
                }
                catch (IOException e)
                {
                    Logger?.Log(LogEventLevel.Error, "ImageLoader image resolution failed: {0}", e);
                    return null;
                }
                catch (InvalidOperationException e)
                {
                    Logger?.Log(LogEventLevel.Error, "ImageLoader image resolution failed: {0}", e);
                    return null;
                }
            }, CancellationToken.None).ConfigureAwait(true);

            if (bitmap != null)
            {
                if (cts.Token.IsCancellationRequested)
                {
                    bitmap.Dispose();
                }
                else
                {
                    sender.Source = bitmap;
                }
            }
        }
        finally
        {
            if (CompletePendingOperation(sender, cts))
            {
                SetIsLoading(sender, false);
            }
        }
    }

    private static CancellationTokenSource ReplacePendingOperation(Image image)
    {
        var previous = image.GetValue(PendingOperationProperty);
        var current = new CancellationTokenSource();
        image.SetValue(PendingOperationProperty, current);
        if (previous != null)
        {
            CancelBestEffort(previous);
        }

        return current;
    }

    private static bool CompletePendingOperation(Image image, CancellationTokenSource operation)
    {
        var ownsPendingOperation = ReferenceEquals(image.GetValue(PendingOperationProperty), operation);
        if (ownsPendingOperation)
        {
            image.SetValue(PendingOperationProperty, null);
        }

        operation.Dispose();
        return ownsPendingOperation;
    }

    private static void CancelBestEffort(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
    }

    public static string? GetSource(Image element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SourceProperty);
    }

    public static void SetSource(Image element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SourceProperty, value);
    }

    public static bool GetIsLoading(Image element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsLoadingProperty);
    }

    private static void SetIsLoading(Image element, bool value)
    {
        element.SetValue(IsLoadingProperty, value);
    }

    public static readonly AttachedProperty<int> WidthProperty = AvaloniaProperty.RegisterAttached<Image, int>("Width", typeof(ImageLoader));

    public static int GetWidth(Image element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(WidthProperty);
    }

    public static void SetWidth(Image element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WidthProperty, value);
    }

    public static readonly AttachedProperty<int> HeightProperty = AvaloniaProperty.RegisterAttached<Image, int>("Height", typeof(ImageLoader));

    public static int GetHeight(Image element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(HeightProperty);
    }

    public static void SetHeight(Image element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HeightProperty, value);
    }
}
