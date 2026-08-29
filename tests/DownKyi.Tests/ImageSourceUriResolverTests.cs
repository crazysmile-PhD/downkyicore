using System.Net;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;

namespace DownKyi.Tests;

public sealed class ImageSourceUriResolverTests
{
    [Theory]
    [InlineData("images/cover.png")]
    [InlineData("/Resources/video-placeholder.png")]
    public void ResolveExternalRelativeSourceReturnsNull(string source)
    {
        Assert.Null(ImageSourceUriResolver.ResolveExternal(source));
    }

    [Fact]
    public async Task ProvideImageAsyncProtocolRelativeSourceUsesHttpsWithoutFaulting()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler, false);
        using var loader = new BaseWebImageLoader(httpClient, false);

        var bitmap = await loader
            .ProvideImageAsync(
                "//i0.hdslb.com/bfs/archive/cover.jpg",
                TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Null(bitmap);
        Assert.Equal("https://i0.hdslb.com/bfs/archive/cover.jpg", handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ProvideImageAsyncRelativeMissingSourceFailsGracefullyWithoutHttpRequest()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler, false);
        using var loader = new BaseWebImageLoader(httpClient, false);

        var bitmap = await loader.ProvideImageAsync(
            "images/missing-cover.png",
            TestContext.Current.CancellationToken);

        Assert.Null(bitmap);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task ConcurrentRequestsForSameImageShareOneHttpTransfer()
    {
        using var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler, false);
        using var loader = new BaseWebImageLoader(httpClient, false);

        var first = loader.ProvideImageAsync(
            "https://i0.hdslb.com/shared.jpg",
            TestContext.Current.CancellationToken);
        await handler.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = loader.ProvideImageAsync(
            "https://i0.hdslb.com/shared.jpg",
            TestContext.Current.CancellationToken);

        handler.Complete.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CancelingLastWaiterCancelsUnderlyingImageTransfer()
    {
        using var handler = new BlockingHandler();
        using var httpClient = new HttpClient(handler, false);
        using var loader = new BaseWebImageLoader(httpClient, false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var load = loader.ProvideImageAsync(
            "https://i0.hdslb.com/canceled.jpg",
            cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await handler.RequestCanceled.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Complete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestCanceled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            RequestStarted.TrySetResult();
            try
            {
                await Complete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RequestCanceled.TrySetResult();
                throw;
            }
        }
    }
}
