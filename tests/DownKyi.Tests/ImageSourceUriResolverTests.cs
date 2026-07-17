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

        var bitmap = await loader.ProvideImageAsync("//i0.hdslb.com/bfs/archive/cover.jpg");

        Assert.Null(bitmap);
        Assert.Equal("https://i0.hdslb.com/bfs/archive/cover.jpg", handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task ProvideImageAsyncRelativeMissingSourceFailsGracefullyWithoutHttpRequest()
    {
        using var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler, false);
        using var loader = new BaseWebImageLoader(httpClient, false);

        var bitmap = await loader.ProvideImageAsync("images/missing-cover.png");

        Assert.Null(bitmap);
        Assert.Null(handler.RequestUri);
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
}
