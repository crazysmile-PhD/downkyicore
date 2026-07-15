using System.Text;
using System.Web;
using DownKyi.Core.Storage;
using Newtonsoft.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;
using NewtonsoftJsonException = Newtonsoft.Json.JsonException;
using SystemTextJsonException = System.Text.Json.JsonException;

namespace DownKyi.Core.Utils;

public static class ObjectHelper
{
    /// <summary>
    /// 解析二维码登录返回的url，用于设置cookie
    /// </summary>
    /// <param name="redirectUri"></param>
    /// <returns></returns>
    public static IReadOnlyList<DownKyiCookie> ParseCookie(Uri? redirectUri)
    {
        var cookies = new List<DownKyiCookie>();
        if (redirectUri is null) return cookies;

        var queryString = redirectUri.Query;
        var query = HttpUtility.ParseQueryString(queryString);
        cookies = (from item in query.AllKeys.OfType<string>()
                   let value = query[item]
                   where item is not ("Expires" or "gourl")
                   select new DownKyiCookie(item, value, ".bilibili.com")).ToList();
        return cookies;
    }

    /// <summary>
    /// 写入cookies到磁盘
    /// </summary>
    /// <param name="file"></param>
    /// <param name="cookieJar"></param>
    /// <returns></returns>
    public static bool WriteCookiesToDisk(string file, IReadOnlyList<DownKyiCookie> cookieJar)
    {
        return WriteObjectToDisk(file, cookieJar);
    }

    /// <summary>
    /// 从磁盘读取cookie
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static IReadOnlyList<DownKyiCookie>? ReadCookiesFromDisk(string file)
    {
        try
        {
            using Stream stream = File.Open(file, FileMode.Open);
            return JsonSerializer.Deserialize<List<DownKyiCookie>>(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (SystemTextJsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从已打开的流中读取cookie
    /// </summary>
    /// <param name="stream"></param>
    /// <returns></returns>
    public static IReadOnlyList<DownKyiCookie>? ReadCookiesFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            var str = streamReader.ReadToEnd();
            return JsonConvert.DeserializeObject<List<DownKyiCookie>>(str);
        }
        catch (IOException)
        {
            return null;
        }
        catch (NewtonsoftJsonException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 写入序列化对象到磁盘
    /// </summary>
    /// <param name="file"></param>
    /// <param name="obj"></param>
    /// <returns></returns>
    public static bool WriteObjectToDisk(string file, object obj)
    {
        try
        {
            using Stream stream = File.Create(file);
            JsonSerializer.Serialize(stream, obj);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SystemTextJsonException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
