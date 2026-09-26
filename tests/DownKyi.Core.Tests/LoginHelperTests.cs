using System.Collections.Concurrent;
using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;
using DownKyi.Core.Utils;

namespace DownKyi.Core.Tests;

public sealed class LoginHelperTests
{
    [Fact]
    public void PersistedWireCookieValueSurvivesReloadWithoutAdditionalEncoding()
    {
        var cookies = new[]
        {
            new DownKyiCookie(
                "SESSDATA",
                "fixture%2Fvalue",
                ".bilibili.com",
                isWireValue: true)
        };

        Assert.True(LoginHelper.SaveLoginInfoCookies(cookies));

        var reloaded = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("fixture%2Fvalue", reloaded.Value);
        Assert.True(reloaded.IsWireValue);
        Assert.Equal("SESSDATA=fixture%2Fvalue", LoginHelper.GetLoginInfoCookiesString());
    }

    [Fact]
    public void LegacyDecodedCookieValueRetainsExistingWireEncodingContract()
    {
        var cookies = new[]
        {
            new DownKyiCookie("SESSDATA", "fixture/value", ".bilibili.com")
        };

        Assert.True(LoginHelper.SaveLoginInfoCookies(cookies));

        var reloaded = Assert.Single(LoginHelper.GetLoginInfoCookies());
        Assert.Equal("fixture%2fvalue", reloaded.Value);
        Assert.True(reloaded.IsWireValue);
        Assert.Equal("SESSDATA=fixture%2fvalue", LoginHelper.GetLoginInfoCookiesString());
    }

    [Fact]
    public void InvalidatingLoginInfoCacheReloadsExternallyWrittenCookies()
    {
        var initialCookies = new[]
        {
            new DownKyiCookie("SESSDATA", "initial", ".bilibili.com", isWireValue: true)
        };
        var updatedCookies = new[]
        {
            new DownKyiCookie("SESSDATA", "updated", ".bilibili.com", isWireValue: true)
        };

        Assert.True(LoginHelper.SaveLoginInfoCookies(initialCookies));
        Assert.Equal("SESSDATA=initial", LoginHelper.GetLoginInfoCookiesString());
        Assert.True(ObjectHelper.WriteCookiesToDisk(ApplicationStorage.GetLogin(), updatedCookies));
        Assert.Equal("SESSDATA=initial", LoginHelper.GetLoginInfoCookiesString());

        LoginHelper.InvalidateLoginInfoCache();

        Assert.Equal("SESSDATA=updated", LoginHelper.GetLoginInfoCookiesString());
    }

    [Fact]
    public void InvalidCookieFileKeepsTheLastSnapshotDirtyForRetry()
    {
        var initialCookies = new[]
        {
            new DownKyiCookie("SESSDATA", "initial", ".bilibili.com", isWireValue: true)
        };
        var updatedCookies = new[]
        {
            new DownKyiCookie("SESSDATA", "updated", ".bilibili.com", isWireValue: true)
        };

        Assert.True(LoginHelper.SaveLoginInfoCookies(initialCookies));
        Assert.Equal("SESSDATA=initial", LoginHelper.GetLoginInfoCookiesString());
        File.WriteAllText(ApplicationStorage.GetLogin(), "[{");
        LoginHelper.InvalidateLoginInfoCache();

        Assert.Equal("SESSDATA=initial", LoginHelper.GetLoginInfoCookiesString());
        Assert.True(ObjectHelper.WriteCookiesToDisk(ApplicationStorage.GetLogin(), updatedCookies));
        Assert.Equal("SESSDATA=updated", LoginHelper.GetLoginInfoCookiesString());
    }

    [Fact]
    public async Task ConcurrentInvalidationReturnsOneCompleteCacheSnapshot()
    {
        const string expectedHeader = "SESSDATA=stable";
        var cookies = new[]
        {
            new DownKyiCookie("SESSDATA", "stable", ".bilibili.com", isWireValue: true)
        };
        var unexpectedHeaders = new ConcurrentQueue<string>();
        using var start = new ManualResetEventSlim();

        Assert.True(LoginHelper.SaveLoginInfoCookies(cookies));

        var invalidator = Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            for (var index = 0; index < 1_000; index++)
            {
                LoginHelper.InvalidateLoginInfoCache();
                Thread.Yield();
            }
        }, TestContext.Current.CancellationToken);
        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                for (var index = 0; index < 250; index++)
                {
                    var header = LoginHelper.GetLoginInfoCookiesString();
                    if (!string.Equals(header, expectedHeader, StringComparison.Ordinal))
                    {
                        unexpectedHeaders.Enqueue(header);
                    }
                }
            }, TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(invalidator));

        Assert.Empty(unexpectedHeaders);
    }

    [Fact]
    public async Task LogoutDeletesTheOwnedLoginFileAndClearsTheInjectedUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"downkyi-logout-{Guid.NewGuid():N}");
        var loginPath = Path.Combine(directory, "login.json");
        var settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(loginPath, "test-login", TestContext.Current.CancellationToken);
            using var store = new SettingsStore(settingsPath);
            store.Update(settings => settings with
            {
                User = settings.User with
                {
                    Mid = 42,
                    Name = "test-user",
                    IsLogin = true,
                    IsVip = true
                }
            });

            var result = LoginHelper.Logout(store, loginPath);
            await store.FlushAsync(TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.False(File.Exists(loginPath));
            var user = store.Current.User;
            Assert.Equal(-1, user.Mid);
            Assert.False(user.IsLogin);
            Assert.False(user.IsVip);
            Assert.Empty(user.Name);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
