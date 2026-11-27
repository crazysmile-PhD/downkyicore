using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

internal class DiskCachedWebImageLoader : BaseWebImageLoader
{
    private readonly string _cacheFolder;

    public DiskCachedWebImageLoader(HttpClient httpClient, bool disposeHttpClient, string cacheFolder = "Cache/Images/") : base(httpClient, disposeHttpClient)
    {
        _cacheFolder = cacheFolder;
    }

    /// <inheritdoc />
    protected override Bitmap? LoadFromGlobalCache(string url, PixelSize? decodeSize)
    {
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));

        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return DecodeBitmap(stream, decodeSize);
        }
        catch (ArgumentException)
        {
            // Encoded cache bytes are disposable; an incomplete older write must not
            // prevent the authoritative HTTP source from being loaded again.
            File.Delete(path);
            return null;
        }
    }

    protected override async Task SaveToGlobalCache(string url, byte[] imageBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));
        if (File.Exists(path)) return;
        Directory.CreateDirectory(_cacheFolder);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, imageBytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another writer already published a complete image for this key.
                return;
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    protected static string CreateCacheKey(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }
}
