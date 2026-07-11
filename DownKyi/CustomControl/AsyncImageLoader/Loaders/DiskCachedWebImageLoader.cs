using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace DownKyi.CustomControl.AsyncImageLoader.Loaders;

public class DiskCachedWebImageLoader : BaseWebImageLoader
{
    private readonly string _cacheFolder;

    public DiskCachedWebImageLoader(string cacheFolder = "Cache/Images/")
    {
        _cacheFolder = cacheFolder;
    }

    public DiskCachedWebImageLoader(HttpClient httpClient, bool disposeHttpClient, string cacheFolder = "Cache/Images/") : base(httpClient, disposeHttpClient)
    {
        _cacheFolder = cacheFolder;
    }

    /// <inheritdoc />
    protected override Bitmap? LoadFromGlobalCache(string url)
    {
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));

        return File.Exists(path) ? new Bitmap(path) : null;
    }

    protected override async Task SaveToGlobalCache(string url, byte[] imageBytes)
    {
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));
        Directory.CreateDirectory(_cacheFolder);
        await File.WriteAllBytesAsync(path, imageBytes).ConfigureAwait(false);
    }

    protected static string CreateCacheKey(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }
}
