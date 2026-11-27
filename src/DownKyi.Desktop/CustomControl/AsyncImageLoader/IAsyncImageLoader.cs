using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader;

internal interface IAsyncImageLoader : IDisposable
{
    /// <summary>
    ///     Loads image
    /// </summary>
    /// <param name="url">Target url</param>
    /// <param name="decodeSize">Requested physical pixels; a zero dimension is unconstrained.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>Bitmap</returns>
    public Task<Bitmap?> ProvideImageAsync(string url, PixelSize? decodeSize = null, CancellationToken cancellationToken = default);
}
