using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        AvaloniaProperty.RegisterAttached<AvaloniaObject, CancellationTokenSource?>(
            "PendingOperation",
            typeof(ImageLoader));

    private static readonly AttachedProperty<bool> NeedsLoadProperty =
        AvaloniaProperty.RegisterAttached<Image, bool>("NeedsLoad", typeof(ImageLoader));

    public static IAsyncImageLoader AsyncImageLoader { get; set; } = NullAsyncImageLoader.Instance;

    static ImageLoader()
    {
        SourceProperty.Changed.AddClassHandler<Image>(OnSourceChanged);
    }

    private static void OnSourceChanged(Image sender, AvaloniaPropertyChangedEventArgs args)
    {
        sender.AttachedToVisualTree -= OnImageAttached;
        sender.DetachedFromVisualTree -= OnImageDetached;
        CancelPendingImageOperation(sender);
        var url = args.GetNewValue<string?>();
        sender.SetValue(NeedsLoadProperty, url != null);
        if (url == null)
        {
            sender.Source = null;
            return;
        }

        sender.AttachedToVisualTree += OnImageAttached;
        sender.DetachedFromVisualTree += OnImageDetached;
        if (sender.IsAttachedToVisualTree()) _ = LoadImageAsync(sender, url);
    }

    private static void OnImageAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Image image && image.GetValue(NeedsLoadProperty) && GetSource(image) is { } url)
        {
            _ = LoadImageAsync(image, url);
        }
    }

    private static void OnImageDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is Image image) CancelPendingImageOperation(image);
    }

    private static void CancelPendingImageOperation(Image image)
    {
        var pending = image.GetValue(PendingOperationProperty);
        image.SetValue(PendingOperationProperty, null);
        if (pending != null) CancelBestEffort(pending);
        SetIsLoading(image, false);
    }

    private static async Task LoadImageAsync(Image sender, string url)
    {
        var cts = ReplacePendingOperation(sender);
        SetIsLoading(sender, true);
        try
        {
            var targetSize = await Dispatcher.UIThread.InvokeAsync(
                () => GetDecodeSize(sender), DispatcherPriority.Loaded, cts.Token);

            var bitmap = await Task.Run(async () =>
            {
                try
                {
                    return await AsyncImageLoader.ProvideImageAsync(url, targetSize, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
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

            if (!cts.IsCancellationRequested) sender.SetValue(NeedsLoadProperty, false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (CompletePendingOperation(sender, cts))
            {
                SetIsLoading(sender, false);
            }
        }
    }

    private static PixelSize? GetDecodeSize(Image image)
    {
        // An Image without a Source arranges itself to zero even inside a fixed
        // slot. Use the framework's allocation, including margin and constraints.
        var available = LayoutInformation.GetPreviousArrangeBounds(image)?.Size
            ?? LayoutInformation.GetPreviousMeasureConstraint(image)
            ?? image.Bounds.Size;
        var size = LayoutHelper.ApplyLayoutConstraints(image, available.Deflate(image.Margin));
        var width = double.IsFinite(size.Width) && size.Width > 0 ? size.Width : 0;
        var height = double.IsFinite(size.Height) && size.Height > 0 ? size.Height : 0;
        if (width <= 0 && height <= 0) return null;
        var scale = LayoutHelper.GetLayoutScale(image);
        return new PixelSize(
            width > 0 ? checked((int)Math.Ceiling(width * scale)) : 0,
            height > 0 ? checked((int)Math.Ceiling(height * scale)) : 0);
    }

    internal static CancellationTokenSource ReplacePendingOperation(AvaloniaObject image)
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

    internal static bool CompletePendingOperation(AvaloniaObject image, CancellationTokenSource operation)
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

}
