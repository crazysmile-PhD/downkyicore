using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal class BaseWebImageLoader : IAsyncImageLoader
{
    private readonly ParametrizedLogger? _logger;
    private readonly bool _shouldDisposeHttpClient;

    /// <summary>
    ///     Initializes a new instance with new <see cref="HttpClient" /> instance
    /// </summary>
    public BaseWebImageLoader() : this(new HttpClient(), true)
    {
    }

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
    public virtual async Task<Bitmap?> ProvideImageAsync(string url)
    {
        return await LoadAsync(url).ConfigureAwait(false);
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
    protected virtual async Task<Bitmap?> LoadAsync(string url)
    {
        var internalOrCachedBitmap = LoadFromLocal(url)
                                     ?? LoadFromInternal(url)
                                     ?? LoadFromGlobalCache(url);
        if (internalOrCachedBitmap != null) return internalOrCachedBitmap;

        try
        {
            var externalBytes = await LoadDataFromExternalAsync(url).ConfigureAwait(false);
            if (externalBytes == null) return null;

            using var memoryStream = new MemoryStream(externalBytes);
            var bitmap = new Bitmap(memoryStream);
            await SaveToGlobalCache(url, externalBytes).ConfigureAwait(false);
            return bitmap;
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
    private static Bitmap? LoadFromLocal(string url)
    {
        return File.Exists(url) ? new Bitmap(url) : null;
    }

    /// <summary>
    ///     Receives image bytes from an internal source (for example, from the disk).
    ///     This data will be NOT cached globally (because it is assumed that it is already in internal source us and does not
    ///     require global caching)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Bitmap</returns>
    protected virtual Bitmap? LoadFromInternal(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            var uri = url.StartsWith('/')
                ? new Uri(url, UriKind.Relative)
                : new Uri(url, UriKind.RelativeOrAbsolute);

            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                return null;

            if (uri is { IsAbsoluteUri: true, IsFile: true })
                return new Bitmap(uri.LocalPath);

            return new Bitmap(AssetLoader.Open(uri));
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
    }

    /// <summary>
    ///     Receives image bytes from an external source (for example, from the Internet).
    ///     This data will be cached globally (if required by the current implementation)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <returns>Image bytes</returns>
    protected virtual async Task<byte[]?> LoadDataFromExternalAsync(string url)
    {
        try
        {
            return await HttpClient.GetByteArrayAsync(new Uri(url, UriKind.Absolute)).ConfigureAwait(false);
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
    protected virtual Bitmap? LoadFromGlobalCache(string url)
    {
        // Current implementation does not provide global caching
        return null;
    }

    /// <summary>
    ///     Attempts to load image from global cache (if it is stored before)
    /// </summary>
    /// <param name="url">Target url</param>
    /// <param name="imageBytes">Bytes to save</param>
    /// <returns>Bitmap</returns>
    protected virtual Task SaveToGlobalCache(string url, byte[] imageBytes)
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
