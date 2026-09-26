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
    private static readonly string LocalLoginInfo = ApplicationStorage.GetLogin();

    // 内存缓存：读多写少，使用 ReaderWriterLockSlim 保证线程安全
    private static readonly ReaderWriterLockSlim CacheLock = new();
    private static LoginInfoSnapshot? _cachedLoginInfo;

    private sealed record LoginInfoSnapshot(
        ImmutableArray<DownKyiCookie> Cookies,
        string CookieHeader,
        bool NeedsRefresh);

    private static DownKyiCookie CloneCookie(DownKyiCookie cookie)
    {
        return new DownKyiCookie(
            cookie.Name,
            cookie.Value,
            cookie.Domain,
            cookie.IsWireValue);
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

            deduplicated[name] = new DownKyiCookie(
                name,
                cookie.Value,
                cookie.Domain,
                cookie.IsWireValue);
        }

        return string.Join("; ", order.Select(name =>
        {
            var cookie = deduplicated[name];
            var value = cookie.IsWireValue
                ? cookie.Value
                : HttpUtility.UrlEncode(cookie.Value);
            return $"{cookie.Name}={value}";
        }));
    }

    /// <summary>
    /// 使登录信息缓存失效，使下一次读取重新加载持久化内容
    /// </summary>
    public static void InvalidateLoginInfoCache()
    {
        CacheLock.EnterWriteLock();
        try
        {
            if (_cachedLoginInfo != null)
            {
                _cachedLoginInfo = _cachedLoginInfo with { NeedsRefresh = true };
            }
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
                File.Move(tempFile, LocalLoginInfo, overwrite: true);
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
            InvalidateLoginInfoCache();
        }
        else
        {
            TryDeleteTemporaryFile(tempFile);
        }

        return isSucceed;
    }

    /// <summary>
    /// 获得登录的cookies，结果会被缓存到内存中，直到登录文件变更使缓存失效
    /// </summary>
    /// <returns></returns>
    public static IReadOnlyList<DownKyiCookie> GetLoginInfoCookies()
    {
        var snapshot = GetOrLoadLoginInfoSnapshot();
        return snapshot == null ? [] : CloneCookies(snapshot.Cookies);
    }

    /// <summary>
    /// 返回登录信息的cookies的字符串，结果会被缓存到内存中，直到登录文件变更使缓存失效
    /// </summary>
    /// <returns></returns>
    public static string GetLoginInfoCookiesString()
    {
        return GetOrLoadLoginInfoSnapshot()?.CookieHeader ?? string.Empty;
    }

    private static LoginInfoSnapshot? GetOrLoadLoginInfoSnapshot()
    {
        CacheLock.EnterReadLock();
        try
        {
            if (_cachedLoginInfo is { NeedsRefresh: false })
            {
                return _cachedLoginInfo;
            }
        }
        finally
        {
            CacheLock.ExitReadLock();
        }

        CacheLock.EnterWriteLock();
        try
        {
            if (_cachedLoginInfo is { NeedsRefresh: false })
            {
                return _cachedLoginInfo;
            }

            if (!File.Exists(LocalLoginInfo))
            {
                _cachedLoginInfo = new LoginInfoSnapshot([], string.Empty, NeedsRefresh: false);
                return _cachedLoginInfo;
            }

            List<DownKyiCookie>? cookies;
            try
            {
                using var stream = new FileStream(LocalLoginInfo, FileMode.Open, FileAccess.Read, FileShare.Read);
                cookies = ObjectHelper.ReadCookiesFromStream(stream)?
                    .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                    .Select(cookie => new DownKyiCookie(
                        cookie.Name.Trim(),
                        cookie.IsWireValue
                            ? cookie.Value
                            : HttpUtility.UrlEncode(cookie.Value),
                        cookie.Domain,
                        isWireValue: true))
                    .ToList();
            }
            catch (IOException)
            {
                return _cachedLoginInfo;
            }
            catch (UnauthorizedAccessException)
            {
                return _cachedLoginInfo;
            }

            if (cookies == null)
            {
                return _cachedLoginInfo;
            }

            var cachedCookies = cookies
                .Select(CloneCookie)
                .ToImmutableArray();
            var snapshot = new LoginInfoSnapshot(
                cachedCookies,
                BuildCookieHeader(cachedCookies),
                NeedsRefresh: false);
            _cachedLoginInfo = snapshot;
            return snapshot;
        }
        finally
        {
            CacheLock.ExitWriteLock();
        }
    }

    public static bool DeleteLoginInfoCookies()
    {
        try
        {
            File.Delete(LocalLoginInfo);
            InvalidateLoginInfoCache();
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
            InvalidateLoginInfoCache();

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
