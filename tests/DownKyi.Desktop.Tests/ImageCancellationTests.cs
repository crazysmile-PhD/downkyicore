using System.Net;
using Avalonia.Controls;
using Avalonia.Media;
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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestToken = cancellationToken;
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested) Canceled.SetResult();
            }
        }
    }
}
