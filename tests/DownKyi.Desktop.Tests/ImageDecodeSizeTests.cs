using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DownKyi.CustomControl.AsyncImageLoader;

namespace DownKyi.Desktop.Tests;

public sealed class ImageDecodeSizeTests
{
    [AvaloniaFact]
    public async Task DeclaredDimensionsReachDecoderBeforeFirstMeasure()
    {
        using var loader = new CapturingLoader();
        var preceding = ImageLoader.AsyncImageLoader;
        ImageLoader.AsyncImageLoader = loader;
        try
        {
            var image = new Image { Width = 64, Height = 64 };
            Assert.Equal(default, image.DesiredSize);
            ImageLoader.SetSource(image, "https://images.invalid/avatar");

            var size = await loader.Requested.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(new PixelSize(64, 64), size);
        }
        finally
        {
            ImageLoader.AsyncImageLoader = preceding;
        }
    }

    [AvaloniaFact]
    public async Task FlexibleImageUsesItsFirstLayoutSlotForDecoding()
    {
        using var loader = new CapturingLoader();
        var preceding = ImageLoader.AsyncImageLoader;
        ImageLoader.AsyncImageLoader = loader;
        var image = new Image();
        var window = new Window { Width = 96, Height = 64, Content = image };
        try
        {
            ImageLoader.SetSource(image, "https://images.invalid/cover");
            window.Show();
            window.UpdateLayout();

            var size = await loader.Requested.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var slot = LayoutInformation.GetPreviousArrangeBounds(image)!.Value.Size;
            Assert.Equal(new PixelSize(
                (int)Math.Ceiling(slot.Width * window.DesktopScaling),
                (int)Math.Ceiling(slot.Height * window.DesktopScaling)), size);
            Assert.True(size is { Width: > 0, Height: > 0 });
        }
        finally
        {
            window.Close();
            ImageLoader.AsyncImageLoader = preceding;
        }
    }

    [AvaloniaFact]
    public async Task BrushDimensionsReachTheSameDecoderContract()
    {
        using var loader = new CapturingLoader();
        var preceding = ImageBrushLoader.AsyncImageLoader;
        ImageBrushLoader.AsyncImageLoader = loader;
        try
        {
            var brush = new ImageBrush();
            ImageBrushLoader.SetWidth(brush, 160);
            ImageBrushLoader.SetHeight(brush, 90);
            ImageBrushLoader.SetSource(brush, "https://images.invalid/brush");

            var size = await loader.Requested.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(new PixelSize(160, 90), size);
        }
        finally
        {
            ImageBrushLoader.AsyncImageLoader = preceding;
        }
    }

    private sealed class CapturingLoader : IAsyncImageLoader
    {
        public TaskCompletionSource<PixelSize?> Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Bitmap?> ProvideImageAsync(string url, PixelSize? decodeSize = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requested.TrySetResult(decodeSize);
            return Task.FromResult<Bitmap?>(null);
        }

        public void Dispose() { }
    }
}
