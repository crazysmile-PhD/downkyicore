using System.Net;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DownKyi.CustomControl.AsyncImageLoader;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;

namespace DownKyi.Desktop.Tests;

public sealed class ImageCancellationTests
{
    [AvaloniaFact]
    public async Task ClearingImageSourceCancelsItsStartedHttpRequest()
    {
        var window = new Window();
        try
        {
            await AssertStartedRequestIsCanceledAsync(loader =>
            {
                ImageLoader.AsyncImageLoader = loader;
                var image = new Image();
                window.Content = image;
                window.Show();
                window.UpdateLayout();
                return source => ImageLoader.SetSource(image, source);
            }).ConfigureAwait(true);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClearingBrushSourceCancelsItsStartedHttpRequest()
    {
        await AssertStartedRequestIsCanceledAsync(loader =>
        {
            ImageBrushLoader.AsyncImageLoader = loader;
            var brush = new ImageBrush();
            return source => ImageBrushLoader.SetSource(brush, source);
        }).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task DetachingCancelsHttpAndReattachingRestartsTheUnchangedSource()
    {
        await AssertDetachAndReattachAsync(changeSourceWhileDetached: false).ConfigureAwait(true);
    }

    [AvaloniaFact]
    public async Task RapidReattachmentLoadsTheNewSourceWithoutRevivingTheCanceledRequest()
    {
        await AssertDetachAndReattachAsync(changeSourceWhileDetached: true).ConfigureAwait(true);
    }

    private static async Task AssertDetachAndReattachAsync(bool changeSourceWhileDetached)
    {
        using var handler = new PendingImageHandler();
        using var client = new HttpClient(handler);
        using var loader = new BaseWebImageLoader(client, false);
        var original = ImageLoader.AsyncImageLoader;
        var image = new Image { Width = 64, Height = 64 };
        var window = new Window { Content = image };
        try
        {
            ImageLoader.AsyncImageLoader = loader;
            window.Show();
            window.UpdateLayout();
            const string firstUrl = "https://images.invalid/first";
            const string secondUrl = "https://images.invalid/second";
            ImageLoader.SetSource(image, firstUrl);
            var first = await handler.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            window.Content = null;
            Assert.True(first.Token.IsCancellationRequested);
            Assert.False(ImageLoader.GetIsLoading(image));
            if (changeSourceWhileDetached) ImageLoader.SetSource(image, secondUrl);
            window.Content = image;
            window.UpdateLayout();
            var second = await handler.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await first.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.Equal(changeSourceWhileDetached ? secondUrl : firstUrl, second.Address);
            Assert.False(second.Token.IsCancellationRequested);
            Assert.True(ImageLoader.GetIsLoading(image));
            Assert.False(handler.Requests.Reader.TryRead(out _));
            ImageLoader.SetSource(image, null);
            await second.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.False(ImageLoader.GetIsLoading(image));
        }
        finally
        {
            ImageLoader.SetSource(image, null);
            window.Close();
            ImageLoader.AsyncImageLoader = original;
        }
    }

    [AvaloniaFact]
    public async Task CompletedImageIsReusedAfterReattachment()
    {
        using var loader = new CompletedImageLoader();
        var original = ImageLoader.AsyncImageLoader;
        var image = new Image { Width = 64, Height = 64 };
        var window = new Window { Content = image };
        var assigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        image.PropertyChanged += (_, args) =>
        {
            if (args.Property == Image.SourceProperty && image.Source != null) assigned.TrySetResult();
        };
        try
        {
            ImageLoader.AsyncImageLoader = loader;
            window.Show();
            window.UpdateLayout();
            ImageLoader.SetSource(image, "https://images.invalid/completed");
            await assigned.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var completed = image.Source;

            window.Content = null;
            window.Content = image;
            window.UpdateLayout();

            Assert.Same(completed, image.Source);
            Assert.False(ImageLoader.GetIsLoading(image));
            Assert.Equal(1, loader.Calls);
        }
        finally
        {
            ImageLoader.SetSource(image, null);
            window.Close();
            ImageLoader.AsyncImageLoader = original;
        }
    }

    private static async Task AssertStartedRequestIsCanceledAsync(
        Func<IAsyncImageLoader, Action<string?>> createSourceSetter)
    {
        using var handler = new PendingImageHandler();
        using var client = new HttpClient(handler);
        using var loader = new BaseWebImageLoader(client, false);
        var previousImageLoader = ImageLoader.AsyncImageLoader;
        var previousBrushLoader = ImageBrushLoader.AsyncImageLoader;
        try
        {
            var setSource = createSourceSetter(loader);
            setSource("https://images.invalid/pending");
            await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            setSource(null);

            await handler.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.True(handler.RequestToken.IsCancellationRequested);
        }
        finally
        {
            ImageLoader.AsyncImageLoader = previousImageLoader;
            ImageBrushLoader.AsyncImageLoader = previousBrushLoader;
        }
    }

    private sealed class PendingImageHandler : HttpMessageHandler
    {
        public Channel<PendingImageRequest> Requests { get; } = Channel.CreateUnbounded<PendingImageRequest>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestToken = cancellationToken;
            var pending = new PendingImageRequest(request.RequestUri!.AbsoluteUri, cancellationToken);
            Requests.Writer.TryWrite(pending);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    pending.Canceled.TrySetResult();
                    Canceled.TrySetResult();
                }
            }
        }
    }

    private sealed record PendingImageRequest(string Address, CancellationToken Token)
    {
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CompletedImageLoader : IAsyncImageLoader
    {
        private readonly WriteableBitmap _bitmap = new(new PixelSize(1, 1), new Vector(96, 96));
        public int Calls { get; private set; }

        public Task<Bitmap?> ProvideImageAsync(string url, PixelSize? decodeSize = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Bitmap?>(_bitmap);
        }

        public void Dispose() => _bitmap.Dispose();
    }
}
