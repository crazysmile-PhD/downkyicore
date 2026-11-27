using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader;

internal sealed class NullAsyncImageLoader : IAsyncImageLoader
{
    public static NullAsyncImageLoader Instance { get; } = new();

    private NullAsyncImageLoader()
    {
    }

    public Task<Bitmap?> ProvideImageAsync(string url, PixelSize? decodeSize = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Bitmap?>(null);
    }

    public void Dispose()
    {
    }
}
