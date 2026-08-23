using DownKyi.Core.BiliApi.Login;
using DownKyi.Core.Settings;
using DownKyi.Core.Storage;

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
