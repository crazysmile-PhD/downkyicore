using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DownKyi.CustomControl.AsyncImageLoader;

namespace DownKyi.Desktop.Tests;

public sealed class ImageDecodeSizeTests
{
    [AvaloniaFact]
    public Task SourceAssignedBeforeAttachmentUsesUnitScaling()
        => AssertPhysicalDecodeSizeAsync(1, 64);

    [AvaloniaFact]
    public Task SourceAssignedBeforeAttachmentUsesFractionalScaling()
        => AssertPhysicalDecodeSizeAsync(1.25, 80);

    [AvaloniaFact]
    public Task SourceAssignedBeforeAttachmentUsesDoubleScaling()
        => AssertPhysicalDecodeSizeAsync(2, 128);

    private static async Task AssertPhysicalDecodeSizeAsync(double scale, int expectedPixels)
    {
        using var loader = new CapturingLoader();
        var preceding = ImageLoader.AsyncImageLoader;
        ImageLoader.AsyncImageLoader = loader;
        var image = new Image { Width = 64, Height = 64 };
        var window = new Window();
        try
        {
            window.SetRenderScaling(scale);
            Assert.Equal(default, image.DesiredSize);
            Assert.Null(TopLevel.GetTopLevel(image));
            ImageLoader.SetSource(image, "https://images.invalid/avatar");
            window.Content = image;
            window.Show();
            window.UpdateLayout();

            var size = await loader.Requested.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(scale, window.RenderScaling);
            Assert.Equal(new PixelSize(expectedPixels, expectedPixels), size);
        }
        finally
        {
            window.Close();
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
