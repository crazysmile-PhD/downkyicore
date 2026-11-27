using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal class BaseWebImageLoader : IAsyncImageLoader
{
    private readonly ParametrizedLogger? _logger;
    private readonly bool _shouldDisposeHttpClient;

    /// <summary>
    ///     Initializes a new instance with the provided <see cref="HttpClient" />, and specifies whether that
    ///     <see cref="HttpClient" /> should be disposed when this instance is disposed.
    /// </summary>
    /// <param name="httpClient">The HttpMessageHandler responsible for processing the HTTP response messages.</param>
    /// <param name="disposeHttpClient">
    ///     true if the inner handler should be disposed of by Dispose; false if you intend to
    ///     reuse the HttpClient.
    /// </param>
    public BaseWebImageLoader(HttpClient httpClient, bool disposeHttpClient)
    {
        HttpClient = httpClient;
        _shouldDisposeHttpClient = disposeHttpClient;
        _logger = Logger.TryGet(LogEventLevel.Error, ImageLoader.AsyncImageLoaderLogArea);
    }

    protected HttpClient HttpClient { get; }

    /// <inheritdoc />
    public virtual async Task<Bitmap?> ProvideImageAsync(string url, PixelSize? decodeSize = null, CancellationToken cancellationToken = default)
    {
        return await LoadAsync(url, decodeSize, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Attempts to load bitmap
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Bitmap</returns>
    protected virtual async Task<Bitmap?> LoadAsync(string url, PixelSize? decodeSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var internalOrCachedBitmap = ImageSourceUriResolver.ResolveExternal(url) == null
                ? LoadFromLocal(url, decodeSize) ?? LoadFromInternal(url, decodeSize) ?? LoadFromGlobalCache(url, decodeSize)
                : LoadFromGlobalCache(url, decodeSize);
            if (internalOrCachedBitmap != null) return internalOrCachedBitmap;

            var externalBytes = await LoadDataFromExternalAsync(url, cancellationToken).ConfigureAwait(false);
            if (externalBytes == null) return null;

            cancellationToken.ThrowIfCancellationRequested();
            using var memoryStream = new MemoryStream(externalBytes);
            var bitmap = DecodeBitmap(memoryStream, decodeSize);
            try
            {
                await SaveToGlobalCache(url, externalBytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (HttpRequestException e)
        {
            _logger?.Log(this, "Failed to resolve image: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (IOException e)
        {
            _logger?.Log(this, "Failed to resolve image: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            _logger?.Log(this, "Failed to resolve image: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (ArgumentException e)
        {
            _logger?.Log(this, "Failed to resolve image: {RequestUri}\nException: {Exception}", url, e);

            return null;
        }
    }

    /// <summary>
    /// the url maybe is local file url,so if file exists ,we got a Bitmap
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    private static Bitmap? LoadFromLocal(string url, PixelSize? decodeSize)
    {
        if (!File.Exists(url)) return null;
        using var stream = File.OpenRead(url);
        return DecodeBitmap(stream, decodeSize);
    }

    /// <summary>
    ///     Receives image bytes from an internal source (for example, from the disk).
    ///     This data will be NOT cached globally (because it is assumed that it is already in internal source us and does not
    ///     require global caching)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Bitmap</returns>
    protected virtual Bitmap? LoadFromInternal(string url, PixelSize? decodeSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            if (ImageSourceUriResolver.ResolveExternal(url) != null)
                return null;

            var uri = url.StartsWith('/', StringComparison.Ordinal)
                ? new Uri(url, UriKind.Relative)
                : new Uri(url, UriKind.RelativeOrAbsolute);

            using var stream = uri is { IsAbsoluteUri: true, IsFile: true }
                ? File.OpenRead(uri.LocalPath)
                : AssetLoader.Open(uri);
            return DecodeBitmap(stream, decodeSize);
        }
        catch (UriFormatException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (IOException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (InvalidOperationException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
    }

    /// <summary>
    ///     Receives image bytes from an external source (for example, from the Internet).
    ///     This data will be cached globally (if required by the current implementation)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Image bytes</returns>
    protected virtual async Task<byte[]?> LoadDataFromExternalAsync(string url, CancellationToken cancellationToken)
    {
        var uri = ImageSourceUriResolver.ResolveExternal(url);
        if (uri == null)
            return null;

        try
        {
            return await HttpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.Log(this, "Image request timed out: {Exception}", e);
            return null;
        }
        catch (HttpRequestException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
        catch (InvalidOperationException e)
        {
            _logger?.Log(this,
                "Failed to resolve image from request with uri: {RequestUri}\nException: {Exception}", url, e);
            return null;
        }
    }

    /// <summary>
    ///     Attempts to load image from global cache (if it is stored before)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Bitmap</returns>
    protected virtual Bitmap? LoadFromGlobalCache(string url, PixelSize? decodeSize)
    {
        // Current implementation does not provide global caching
        return null;
    }

    protected static Bitmap DecodeBitmap(Stream stream, PixelSize? decodeSize)
    {
        if (decodeSize is not { } size || size.Width <= 0 && size.Height <= 0)
            return new Bitmap(stream);

        if (size.Width <= 0) return Bitmap.DecodeToHeight(stream, size.Height);
        if (size.Height <= 0) return Bitmap.DecodeToWidth(stream, size.Width);

        // Cover both target dimensions without distorting the source aspect ratio.
        // A second bounded decode is needed only when width alone is insufficient.
        if (!stream.CanSeek)
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered);
            buffered.Position = 0;
            return DecodeBitmap(buffered, size);
        }

        var position = stream.Position;
        var bitmap = Bitmap.DecodeToWidth(stream, size.Width);
        if (bitmap.PixelSize.Height >= size.Height) return bitmap;
        bitmap.Dispose();
        stream.Position = position;
        return Bitmap.DecodeToHeight(stream, size.Height);
    }

    /// <summary>
    ///     Attempts to load image from global cache (if it is stored before)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <param name="imageBytes">Bytes to save</param>
    /// <returns>Bitmap</returns>
    protected virtual Task SaveToGlobalCache(string url, byte[] imageBytes, CancellationToken cancellationToken)
    {
        // Current implementation does not provide global caching
        return Task.CompletedTask;
    }

    ~BaseWebImageLoader()
    {
        Dispose(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && _shouldDisposeHttpClient) HttpClient.Dispose();
    }

}
