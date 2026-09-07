using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal class RamCachedWebImageLoader : BaseWebImageLoader
{
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _memoryCache = new();

    /// <inheritdoc />
    public RamCachedWebImageLoader(HttpClient httpClient, bool disposeHttpClient) : base(httpClient, disposeHttpClient)
    {
    }

    /// <inheritdoc />
    public override async Task<Bitmap?> ProvideImageAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        var loadTask = _memoryCache.GetOrAdd(
            url,
            key => LoadAsync(key, CancellationToken.None));
        Bitmap? bitmap;
        try
        {
            bitmap = await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (loadTask.IsCompleted && !loadTask.IsCompletedSuccessfully)
            {
                _memoryCache.TryRemove(
                    new KeyValuePair<string, Task<Bitmap?>>(url, loadTask));
            }

            throw;
        }

        // If load failed - remove from cache and return
        // Next load attempt will try to load image again
        if (bitmap == null) _memoryCache.TryRemove(url, out _);
        return bitmap;
    }
}
