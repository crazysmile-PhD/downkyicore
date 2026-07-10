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
    protected override Task<Bitmap?> LoadFromGlobalCache(string url)
    {
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));

        return File.Exists(path) ? Task.FromResult<Bitmap?>(new Bitmap(path)) : Task.FromResult<Bitmap?>(null);
    }

#if NETSTANDARD2_1
        protected override async Task SaveToGlobalCache(string url, byte[] imageBytes) {
            var path = Path.Combine(_cacheFolder, CreateCacheKey(url));

            Directory.CreateDirectory(_cacheFolder);
            await File.WriteAllBytesAsync(path, imageBytes).ConfigureAwait(false);
        }
#else
    protected override Task SaveToGlobalCache(string url, byte[] imageBytes)
    {
        var path = Path.Combine(_cacheFolder, CreateCacheKey(url));
        Directory.CreateDirectory(_cacheFolder);
        File.WriteAllBytes(path, imageBytes);
        return Task.CompletedTask;
    }
#endif

    protected static string CreateCacheKey(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }
}
