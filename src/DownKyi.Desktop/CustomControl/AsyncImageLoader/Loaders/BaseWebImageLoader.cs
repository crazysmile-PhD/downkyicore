using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Logging;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal class BaseWebImageLoader : IAsyncImageLoader
{
    private readonly ParametrizedLogger? _logger;
    private readonly bool _shouldDisposeHttpClient;
    private readonly ConcurrentDictionary<string, SharedDownload> _downloads = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim NetworkGate = new(8, 8);

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
    public virtual async Task<Bitmap?> ProvideImageAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return await LoadAsync(url, cancellationToken).ConfigureAwait(false);
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
    protected virtual async Task<Bitmap?> LoadAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var internalOrCachedBitmap = ImageSourceUriResolver.ResolveExternal(url) == null
                ? LoadFromLocal(url) ?? LoadFromInternal(url) ?? LoadFromGlobalCache(url)
                : LoadFromGlobalCache(url);
            if (internalOrCachedBitmap != null) return internalOrCachedBitmap;

            var externalBytes = await LoadSharedDataFromExternalAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (externalBytes == null) return null;

            cancellationToken.ThrowIfCancellationRequested();
            using var memoryStream = new MemoryStream(externalBytes);
            var bitmap = new Bitmap(memoryStream);
            try
            {
                await SaveToGlobalCache(url, externalBytes, cancellationToken).ConfigureAwait(false);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            if (ImageSourceUriResolver.ResolveExternal(url) != null)
                return null;

            var uri = url.StartsWith('/')
                ? new Uri(url, UriKind.Relative)
                : new Uri(url, UriKind.RelativeOrAbsolute);

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
    protected virtual async Task<byte[]?> LoadDataFromExternalAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var uri = ImageSourceUriResolver.ResolveExternal(url);
        if (uri == null)
            return null;

        try
        {
            await NetworkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await HttpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                NetworkGate.Release();
            }
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

    private async Task<byte[]?> LoadSharedDataFromExternalAsync(
        string url,
        CancellationToken cancellationToken)
    {
        SharedDownload download;
        Task<byte[]?> task;
        while (true)
        {
            download = _downloads.GetOrAdd(url, static _ => new SharedDownload());
            if (download.TryAcquire(
                    token => LoadDataFromExternalAsync(url, token),
                    out task))
            {
                break;
            }

            _downloads.TryRemove(new KeyValuePair<string, SharedDownload>(url, download));
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (download.Release())
            {
                _downloads.TryRemove(new KeyValuePair<string, SharedDownload>(url, download));
            }
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
    protected virtual Task SaveToGlobalCache(
        string url,
        byte[] imageBytes,
        CancellationToken cancellationToken)
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
        if (!disposing)
        {
            return;
        }

        foreach (var download in _downloads.Values)
        {
            download.Dispose();
        }

        _downloads.Clear();
        if (_shouldDisposeHttpClient) HttpClient.Dispose();
    }

    private sealed class SharedDownload : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation = new();
        private Task<byte[]?>? _task;
        private int _waiters;
        private bool _closed;

        public bool TryAcquire(
            Func<CancellationToken, Task<byte[]?>> factory,
            out Task<byte[]?> task)
        {
            lock (_sync)
            {
                if (_closed)
                {
                    task = Task.FromCanceled<byte[]?>(new CancellationToken(canceled: true));
                    return false;
                }

                _waiters++;
                _task ??= factory(_cancellation.Token);
                task = _task;
                return true;
            }
        }

        public bool Release()
        {
            lock (_sync)
            {
                _waiters--;
                if (_waiters > 0)
                {
                    return false;
                }

                _closed = true;
                if (_task is { IsCompleted: false })
                {
                    _cancellation.Cancel();
                }

                DisposeCancellationWhenComplete();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (!_cancellation.IsCancellationRequested)
                {
                    _cancellation.Cancel();
                }

                if (_task == null || _task.IsCompleted)
                {
                    _cancellation.Dispose();
                }
                else
                {
                    DisposeCancellationWhenComplete();
                }
            }
        }

        private void DisposeCancellationWhenComplete()
        {
            var task = _task;
            if (task == null || task.IsCompleted)
            {
                _cancellation.Dispose();
                return;
            }

            _ = task.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                _cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

}
