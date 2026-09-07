using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;

namespace DownKyi.CustomControl.AsyncImageLoader;

internal static class ImageBrushLoader
{
    private static readonly ParametrizedLogger? Logger =
        Avalonia.Logging.Logger.TryGet(LogEventLevel.Error, ImageLoader.AsyncImageLoaderLogArea);
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ImageBrush, string?>("Source", typeof(ImageLoader));
    private static readonly AttachedProperty<CancellationTokenSource?> PendingOperationProperty =
        AvaloniaProperty.RegisterAttached<ImageBrush, CancellationTokenSource?>(
            "PendingOperation",
            typeof(ImageBrushLoader));
    public static IAsyncImageLoader AsyncImageLoader { get; set; } = NullAsyncImageLoader.Instance;

    static ImageBrushLoader()
    {
        SourceProperty.Changed.AddClassHandler<ImageBrush>(OnSourceChanged);
    }

    private static void OnSourceChanged(ImageBrush imageBrush, AvaloniaPropertyChangedEventArgs args)
    {
        _ = OnSourceChangedAsync(imageBrush, args);
    }

    private static async Task OnSourceChangedAsync(ImageBrush imageBrush, AvaloniaPropertyChangedEventArgs args)
    {
        var (oldValue, newValue) = args.GetOldAndNewValue<string?>();
        if (oldValue == newValue)
            return;

        var cancellation = ReplacePendingOperation(imageBrush);
        SetIsLoading(imageBrush, true);

        Bitmap? bitmap = null;
        try
        {
            if (newValue is not null)
            {
                // 注意缩放比例
                var width = GetWidth(imageBrush);
                var height = GetHeight(imageBrush);
                PixelSize? targetSize = null;
                if (width > 0 && height > 0)
                {
                    var scale = await Dispatcher.UIThread.InvokeAsync(GetDesktopScaling);
                    targetSize = new PixelSize(
                        Convert.ToInt32(width * scale),
                        Convert.ToInt32(height * scale));
                }

                bitmap = await Task.Run(async () =>
                {
                    var sourceBitmap = await AsyncImageLoader
                        .ProvideImageAsync(newValue, cancellation.Token)
                        .ConfigureAwait(false);
                    if (sourceBitmap == null || !targetSize.HasValue)
                    {
                        return sourceBitmap;
                    }

                    using (sourceBitmap)
                    {
                        return sourceBitmap.CreateScaledBitmap(targetSize.Value);
                    }
                }, CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (HttpRequestException e)
        {
            Logger?.Log("ImageBrushLoader", "ImageBrushLoader image resolution failed: {0}", e);
        }
        catch (IOException e)
        {
            Logger?.Log("ImageBrushLoader", "ImageBrushLoader image resolution failed: {0}", e);
        }
        catch (InvalidOperationException e)
        {
            Logger?.Log("ImageBrushLoader", "ImageBrushLoader image resolution failed: {0}", e);
        }

        finally
        {
            if (GetSource(imageBrush) != newValue)
            {
                bitmap?.Dispose();
            }
            else
            {
                imageBrush.Source = bitmap;
            }

            if (CompletePendingOperation(imageBrush, cancellation))
            {
                SetIsLoading(imageBrush, false);
            }
        }
    }

    private static CancellationTokenSource ReplacePendingOperation(ImageBrush imageBrush)
    {
        var previous = imageBrush.GetValue(PendingOperationProperty);
        var current = new CancellationTokenSource();
        imageBrush.SetValue(PendingOperationProperty, current);
        if (previous != null)
        {
            CancelBestEffort(previous);
        }

        return current;
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

    private static bool CompletePendingOperation(
        ImageBrush imageBrush,
        CancellationTokenSource operation)
    {
        var ownsPendingOperation = ReferenceEquals(
            imageBrush.GetValue(PendingOperationProperty),
            operation);
        if (ownsPendingOperation)
        {
            imageBrush.SetValue(PendingOperationProperty, null);
        }

        operation.Dispose();
        return ownsPendingOperation;
    }

    public static string? GetSource(ImageBrush element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(SourceProperty);
    }

    public static void SetSource(ImageBrush element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SourceProperty, value);
    }

    public static readonly AttachedProperty<bool> IsLoadingProperty = AvaloniaProperty.RegisterAttached<ImageBrush, bool>("IsLoading", typeof(ImageLoader));

    public static bool GetIsLoading(ImageBrush element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(IsLoadingProperty);
    }

    private static void SetIsLoading(ImageBrush element, bool value)
    {
        element.SetValue(IsLoadingProperty, value);
    }

    public static readonly AttachedProperty<int> WidthProperty = AvaloniaProperty.RegisterAttached<ImageBrush, int>("Width", typeof(ImageLoader));

    public static int GetWidth(ImageBrush element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(WidthProperty);
    }

    public static void SetWidth(ImageBrush element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(WidthProperty, value);
    }

    public static readonly AttachedProperty<int> HeightProperty = AvaloniaProperty.RegisterAttached<ImageBrush, int>("Height", typeof(ImageLoader));

    public static int GetHeight(ImageBrush element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.GetValue(HeightProperty);
    }

    public static void SetHeight(ImageBrush element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HeightProperty, value);
    }

    private static double GetDesktopScaling()
    {
        return Avalonia.Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }
            ? mainWindow.DesktopScaling
            : 1d;
    }
}
