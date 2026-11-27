using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using DownKyi.CustomControl.AsyncImageLoader.Loaders;

namespace DownKyi.Tests;

public sealed class ImageDiskCacheTests
{
    [Theory]
    [InlineData(1024, 1024, 64, 64, 64, 64)]
    [InlineData(2048, 1024, 64, 64, 128, 64)]
    [InlineData(1024, 2048, 64, 64, 64, 128)]
    [InlineData(1024, 512, 0, 64, 128, 64)]
    [InlineData(512, 1024, 64, 0, 64, 128)]
    public async Task NetworkAndCachedBytesDecodeToRequestedSizeWithoutChangingCachedOriginal(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight, int expectedWidth, int expectedHeight)
    {
        SkiaPlatform.Initialize();
        var directory = Directory.CreateTempSubdirectory("downkyi-thumbnail-cache-");
        try
        {
            using var source = new WriteableBitmap(new PixelSize(sourceWidth, sourceHeight), new Vector(96, 96));
            using var stream = new MemoryStream();
            source.Save(stream, PngBitmapEncoderOptions.Default);
            using var handler = new ImageHandler(stream.ToArray());
            using var client = new HttpClient(handler);
            using var loader = new DiskCachedWebImageLoader(client, false, directory.FullName);
            const string url = "https://images.invalid/thumbnail.png";
            var target = new PixelSize(targetWidth, targetHeight);
            using (var downloaded = await loader.ProvideImageAsync(url, target, TestContext.Current.CancellationToken))
            {
                Assert.NotNull(downloaded);
                Assert.Equal(new PixelSize(expectedWidth, expectedHeight), downloaded.PixelSize);
            }
            using (var cached = await loader.ProvideImageAsync(url, target, TestContext.Current.CancellationToken))
            {
                Assert.NotNull(cached);
                Assert.Equal(new PixelSize(expectedWidth, expectedHeight), cached.PixelSize);
            }

            Assert.Equal(1, handler.Requests);
            using var original = new Bitmap(Assert.Single(Directory.GetFiles(directory.FullName)));
            Assert.Equal(new PixelSize(sourceWidth, sourceHeight), original.PixelSize);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CorruptCacheIsRefetchedAndConcurrentWritesPublishOnlyCompleteImages()
    {
        // This integration suite has no headless drawing backend. Register the
        // product decoder without changing the separate UI test process.
        SkiaPlatform.Initialize();
        var directory = Directory.CreateTempSubdirectory("downkyi-image-cache-");
        try
        {
            const string url = "https://images.invalid/cover.png";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            var cachePath = Path.Combine(directory.FullName, key);
            var cancellationToken = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(cachePath, "incomplete image", cancellationToken).ConfigureAwait(true);
            using var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96));
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            var png = stream.ToArray();
            using var handler = new ImageHandler(png);
            using var client = new HttpClient(handler);
            using var loader = new DiskCachedWebImageLoader(client, false, directory.FullName);
            using (var recovered = await loader.ProvideImageAsync(url, cancellationToken: cancellationToken).ConfigureAwait(true))
            {
                Assert.NotNull(recovered);
                Assert.Equal(new PixelSize(1, 1), recovered.PixelSize);
                Assert.Equal(1, handler.Requests);
            }
            using (var hit = await loader.ProvideImageAsync(url, cancellationToken: cancellationToken).ConfigureAwait(true))
            {
                Assert.NotNull(hit);
                Assert.Equal(1, handler.Requests);
            }

            using var writer = new CacheWriter(client, directory.FullName);
            File.Delete(cachePath);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                writer.SaveAsync(url, png, cancellationToken))).ConfigureAwait(true);
            using var published = new Bitmap(cachePath);
            Assert.Equal(new PixelSize(1, 1), published.PixelSize);
            Assert.Equal([cachePath], Directory.GetFiles(directory.FullName));

            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync().ConfigureAwait(true);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                writer.SaveAsync(url + "?canceled", png, canceled.Token)).ConfigureAwait(true);
            Assert.Equal([cachePath], Directory.GetFiles(directory.FullName));
            Assert.Equal(png, await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class CacheWriter(HttpClient client, string cacheFolder)
        : DiskCachedWebImageLoader(client, false, cacheFolder)
    {
        public Task SaveAsync(string url, byte[] bytes, CancellationToken cancellationToken) =>
            SaveToGlobalCache(url, bytes, cancellationToken);
    }

    private sealed class ImageHandler(byte[] png) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        }
    }
}
