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
            using (var recovered = await loader.ProvideImageAsync(url, cancellationToken).ConfigureAwait(true))
            {
                Assert.NotNull(recovered);
                Assert.Equal(new PixelSize(1, 1), recovered.PixelSize);
                Assert.Equal(1, handler.Requests);
            }
            using (var hit = await loader.ProvideImageAsync(url, cancellationToken).ConfigureAwait(true))
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
