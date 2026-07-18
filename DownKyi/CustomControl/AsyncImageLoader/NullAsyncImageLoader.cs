using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader;

internal sealed class NullAsyncImageLoader : IAsyncImageLoader
{
    public static NullAsyncImageLoader Instance { get; } = new();

    private NullAsyncImageLoader()
    {
    }

    public Task<Bitmap?> ProvideImageAsync(string url)
    {
        return Task.FromResult<Bitmap?>(null);
    }

    public void Dispose()
    {
    }
}
