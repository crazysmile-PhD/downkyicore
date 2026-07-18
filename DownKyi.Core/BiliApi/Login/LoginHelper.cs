using System.Collections.Immutable;
using System.Web;
using DownKyi.Core.Settings;
using DownKyi.Core.Settings.Models;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;

namespace DownKyi.Core.BiliApi.Login;

public static class LoginHelper
{
    // 本地位置
    private static readonly string LocalLoginInfo = StorageManager.GetLogin();

    // 内存缓存：读多写少，使用 ReaderWriterLockSlim 保证线程安全
    private static readonly ReaderWriterLockSlim CacheLock = new();
    private static ImmutableArray<DownKyiCookie> _cachedCookies = [];
    private static bool _isCookieCacheInitialized;
    private static string? _cachedCookieString;

    private static DownKyiCookie CloneCookie(DownKyiCookie cookie)
    {
        return new DownKyiCookie(cookie.Name, cookie.Value, cookie.Domain);
    }

    private static List<DownKyiCookie> CloneCookies(IEnumerable<DownKyiCookie> cookies)
    {
        return cookies.Select(CloneCookie).ToList();
    }

    public static string BuildCookieHeader(IEnumerable<DownKyiCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        var order = new List<string>();
        var deduplicated = new Dictionary<string, DownKyiCookie>(StringComparer.OrdinalIgnoreCase);

        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name))
            {
                continue;
            }

            var name = cookie.Name.Trim();
            if (!deduplicated.ContainsKey(name))
            {
                order.Add(name);
            }

            deduplicated[name] = new DownKyiCookie(name, cookie.Value, cookie.Domain);
        }

        return string.Join("; ", order.Select(name =>
        {
            var cookie = deduplicated[name];
            return $"{cookie.Name}={cookie.Value}";
        }));
    }

    /// <summary>
    /// 使缓存失效，在写操作完成后调用
    /// </summary>
    private static void InvalidateCache()
    {
        CacheLock.EnterWriteLock();
        try
        {
            _cachedCookies = [];
            _isCookieCacheInitialized = false;
            _cachedCookieString = null;
        }
        finally
        {
            CacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 保存登录的cookies到文件
    /// </summary>
    /// <param name="redirectUri"></param>
    /// <returns></returns>
    public static bool SaveLoginInfoCookies(Uri redirectUri)
    {
        var cookies = ObjectHelper.ParseCookie(redirectUri);

        return SaveLoginInfoCookies(cookies);
    }

    /// <summary>
    /// 保存登录的cookies到文件
    /// </summary>
    /// <param name="cookies"></param>
    /// <returns></returns>
    public static bool SaveLoginInfoCookies(IReadOnlyList<DownKyiCookie> cookies)
    {
        var tempFile = LocalLoginInfo + "-" + Guid.NewGuid().ToString("N");

        var isSucceed = ObjectHelper.WriteCookiesToDisk(tempFile, cookies);
        if (isSucceed)
        {
            try
            {
                File.Copy(tempFile, LocalLoginInfo, true);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            finally
            {
                TryDeleteTemporaryFile(tempFile);
            }

            // 写入成功后使缓存立即失效
            InvalidateCache();
        }
        else
        {
            TryDeleteTemporaryFile(tempFile);
        }

        return isSucceed;
    }

    /// <summary>
    /// 获得登录的cookies，结果会被缓存到内存中，直到下次写操作使缓存失效
    /// </summary>
    /// <returns></returns>
    public static IReadOnlyList<DownKyiCookie> GetLoginInfoCookies()
    {
        // 先尝试从缓存读取
        CacheLock.EnterReadLock();
        try
        {
            if (_isCookieCacheInitialized)
            {
                return CloneCookies(_cachedCookies);
            }
        }
        finally
        {
            CacheLock.ExitReadLock();
        }

        // 缓存未命中，从磁盘加载
        CacheLock.EnterWriteLock();
        try
        {
            // 双重检查：可能其他线程已完成加载
            if (_isCookieCacheInitialized)
            {
                return CloneCookies(_cachedCookies);
            }

            if (!File.Exists(LocalLoginInfo))
            {
                _cachedCookies = [];
                _cachedCookieString = string.Empty;
                _isCookieCacheInitialized = true;
                return [];
            }

            List<DownKyiCookie>? cookies;
            try
            {
                // 直接读取文件，用 FileShare.Read 避免独占锁，无需临时文件
                using var stream = new FileStream(LocalLoginInfo, FileMode.Open, FileAccess.Read, FileShare.Read);
                cookies = ObjectHelper.ReadCookiesFromStream(stream)?
                    .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                    .Select(cookie => new DownKyiCookie(
                        cookie.Name.Trim(),
                        HttpUtility.UrlEncode(cookie.Value),
                        cookie.Domain))
                    .ToList();
            }
            catch (IOException)
            {
                return new List<DownKyiCookie>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<DownKyiCookie>();
            }

            _cachedCookies = (cookies ?? [])
                .Select(CloneCookie)
                .ToImmutableArray();
            _isCookieCacheInitialized = true;
            // 同步更新字符串缓存
            _cachedCookieString = BuildCookieHeader(_cachedCookies);

            return CloneCookies(_cachedCookies);
        }
        finally
        {
            CacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 返回登录信息的cookies的字符串，结果会被缓存到内存中，直到下次写操作使缓存失效
    /// </summary>
    /// <returns></returns>
    public static string GetLoginInfoCookiesString()
    {
        // 先尝试从字符串缓存读取
        CacheLock.EnterReadLock();
        try
        {
            if (_cachedCookieString != null)
            {
                return _cachedCookieString;
            }
        }
        finally
        {
            CacheLock.ExitReadLock();
        }

        // 字符串缓存未命中时，触发完整加载（GetLoginInfoCookies 内部会同步填充字符串缓存）
        GetLoginInfoCookies();

        CacheLock.EnterReadLock();
        try
        {
            return _cachedCookieString ?? "";
        }
        finally
        {
            CacheLock.ExitReadLock();
        }
    }

    /// <summary>
    /// 注销登录
    /// </summary>
    /// <returns></returns>
    public static bool Logout(ISettingsStore settingsStore)
    {
        return Logout(settingsStore, LocalLoginInfo);
    }

    internal static bool Logout(ISettingsStore settingsStore, string loginInfoPath)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(loginInfoPath);
        if (!File.Exists(loginInfoPath)) return false;

        try
        {
            File.Delete(loginInfoPath);

            // 注销后使缓存立即失效
            InvalidateCache();

            settingsStore.Update(settings => settings with
            {
                User = new UserApplicationSettings(
                    Mid: -1,
                    Name: string.Empty,
                    IsLogin: false,
                    IsVip: false,
                    ImgKey: string.Empty,
                    SubKey: string.Empty)
            });
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }
}
